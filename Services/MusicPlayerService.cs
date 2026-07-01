using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YangzaiWorkshop.Models;

namespace YangzaiWorkshop.Services;

public class MusicPlayerService
{
    private static readonly Lazy<MusicPlayerService> _instance = new(() => new MusicPlayerService());
    public static MusicPlayerService Instance => _instance.Value;

    public event Action? StateChanged;
    public event Action? PlaylistChanged;

    private readonly List<string> _playlist = new();
    private int _currentIndex = -1;
    private bool _isPlaying;
    private bool _isMuted;
    private double _volumeBeforeMute = 0.7;
    private double _volume = 0.7;
    private string _playMode = "RepeatAll"; // RepeatAll / RepeatOne / Shuffle
    private readonly Random _rng = new();

    private MediaElement? _media;
    private DispatcherTimer? _timer;

    public IReadOnlyList<string> Playlist => _playlist.AsReadOnly();
    public int CurrentIndex => _currentIndex;
    public bool IsPlaying => _isPlaying;

    /// <summary>是否有活跃曲目（已选择并加载）</summary>
    public bool IsActive => _currentIndex >= 0 && _currentIndex < _playlist.Count;
    public bool IsMuted => _isMuted;
    public double Volume => _volume;
    public string PlayMode => _playMode;

    /// <summary>当前曲目文件名</summary>
    public string? CurrentTrackName =>
        _currentIndex >= 0 && _currentIndex < _playlist.Count
            ? Path.GetFileName(_playlist[_currentIndex]) : null;

    /// <summary>当前进度（秒）</summary>
    public double Position
    {
        get => _media?.Position.TotalSeconds ?? 0;
        set { if (_media != null) _media.Position = TimeSpan.FromSeconds(value); }
    }

    /// <summary>当前总时长（秒）</summary>
    public double Duration =>
        _media?.NaturalDuration.HasTimeSpan == true
            ? _media.NaturalDuration.TimeSpan.TotalSeconds : 0;

    private MusicPlayerService() { }

    /// <summary>初始化 MediaElement 和定时器</summary>
    public void Initialize(MediaElement media)
    {
        _media = media;
        _media.MediaEnded += OnMediaEnded;
        _media.MediaOpened += (_, _) =>
        {
            if (_isPlaying)
            {
                _media.Play();
                StateChanged?.Invoke();
            }
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => StateChanged?.Invoke();
    }

    /// <summary>从配置加载音量</summary>
    public void LoadSettings(AppConfig config)
    {
        _volume = Math.Clamp(config.MusicVolume, 0, 1);
        _isMuted = false;
        _playMode = config.MusicPlayMode switch
        {
            "Shuffle" => "Shuffle",
            "RepeatOne" => "RepeatOne",
            _ => "RepeatAll"
        };
        if (_media != null) _media.Volume = _volume;
        StateChanged?.Invoke();
    }

    /// <summary>保存音量到配置</summary>
    public void SaveSettings(AppConfig config)
    {
        config.MusicVolume = _volume;
        config.MusicPlayMode = _playMode;
    }

    /// <summary>加载音乐目录</summary>
    public void LoadPlaylist(string musicDir)
    {
        _playlist.Clear();
        if (Directory.Exists(musicDir))
        {
            var files = Directory.GetFiles(musicDir)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".mp3" or ".wav" or ".ogg" or ".m4a" or ".flac" or ".aac" or ".wma";
                })
                .OrderBy(f => f)
                .ToList();
            _playlist.AddRange(files);
        }

        if (_playlist.Count == 0)
        {
            _currentIndex = -1;
            Pause();
        }
        else if (_currentIndex >= _playlist.Count)
        {
            _currentIndex = 0;
        }

        PlaylistChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>添加音乐文件</summary>
    public void AddFiles(IEnumerable<string> filePaths, string musicDir)
    {
        FileService.EnsureDirectory(musicDir);
        foreach (var f in filePaths)
        {
            var dest = Path.Combine(musicDir, Path.GetFileName(f));
            if (!File.Exists(dest))
                File.Copy(f, dest, false);
        }
        LoadPlaylist(musicDir);
    }

    /// <summary>删除音乐文件</summary>
    public void DeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var wasCurrent = _currentIndex >= 0 && _currentIndex < _playlist.Count
                    && string.Equals(_playlist[_currentIndex], filePath, StringComparison.OrdinalIgnoreCase);
                File.Delete(filePath);
                if (wasCurrent)
                    Stop();
                LoadPlaylist(FileService.MusicPath(App.WorkRoot));
            }
        }
        catch { }
    }

    /// <summary>播放指定索引</summary>
    public void Play(int index)
    {
        if (_media == null || index < 0 || index >= _playlist.Count) return;
        _currentIndex = index;
        _media.Source = new Uri(_playlist[index]);
        _media.Volume = _isMuted ? 0 : _volume;
        _isPlaying = true;
        _media.Play();
        _timer?.Start();
        StateChanged?.Invoke();
    }

    /// <summary>播放/暂停切换</summary>
    public void TogglePlayPause()
    {
        if (_isPlaying)
            Pause();
        else if (_playlist.Count > 0)
        {
            if (_currentIndex < 0) _currentIndex = 0;
            if (_media?.Source == null)
                Play(_currentIndex);
            else
            {
                _media?.Play();
                _isPlaying = true;
                _timer?.Start();
                StateChanged?.Invoke();
            }
        }
    }

    /// <summary>暂停</summary>
    public void Pause()
    {
        _media?.Pause();
        _isPlaying = false;
        _timer?.Stop();
        StateChanged?.Invoke();
    }

    /// <summary>停止</summary>
    public void Stop()
    {
        _media?.Stop();
        _media?.Close();
        _isPlaying = false;
        _currentIndex = -1;
        _timer?.Stop();
        StateChanged?.Invoke();
    }

    /// <summary>下一首</summary>
    public void Next()
    {
        if (_playlist.Count == 0) return;
        int next;
        if (_playMode == "RepeatOne")
        {
            // 单曲循环时，手动点下一首仍切到下一首
            next = (_currentIndex + 1) % _playlist.Count;
        }
        else if (_playMode == "Shuffle")
        {
            next = _rng.Next(_playlist.Count);
            if (next == _currentIndex && _playlist.Count > 1)
                next = (next + 1) % _playlist.Count;
        }
        else
        {
            next = (_currentIndex + 1) % _playlist.Count;
        }
        Play(next);
    }

    /// <summary>循环模式切换：RepeatAll → RepeatOne → Shuffle</summary>
    public string TogglePlayMode()
    {
        _playMode = _playMode switch
        {
            "RepeatAll" => "RepeatOne",
            "RepeatOne" => "Shuffle",
            _ => "RepeatAll"
        };
        StateChanged?.Invoke();
        return _playMode;
    }

    /// <summary>设置音量</summary>
    public void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, 1);
        if (!_isMuted && _media != null) _media.Volume = _volume;
        StateChanged?.Invoke();
    }

    /// <summary>静音切换</summary>
    public bool ToggleMute()
    {
        _isMuted = !_isMuted;
        if (_media != null)
        {
            if (_isMuted)
            {
                _volumeBeforeMute = _volume;
                _media.Volume = 0;
            }
            else
            {
                _media.Volume = _volumeBeforeMute;
            }
        }
        StateChanged?.Invoke();
        return _isMuted;
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            AdvanceTrack();
        else
            dispatcher.BeginInvoke((Action)AdvanceTrack);
    }

    private void AdvanceTrack()
    {
        if (_playlist.Count == 0) return;
        if (_playMode == "RepeatOne")
        {
            // 单曲循环：重新播放当前曲目
            Play(_currentIndex);
        }
        else if (_playMode == "Shuffle")
        {
            var next = _rng.Next(_playlist.Count);
            if (next == _currentIndex && _playlist.Count > 1)
                next = (next + 1) % _playlist.Count;
            Play(next);
        }
        else
        {
            Play((_currentIndex + 1) % _playlist.Count);
        }
    }
}

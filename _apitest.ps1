$ErrorActionPreference = "Stop"
$img = "C:\Users\CooKu\Desktop\Material\LearningNotes\Yangzai-Workshop\bin\Debug\net8.0-windows\WorkData\Image\小说\新小说\第1章\AI_20260829_090102.png"
$key = "sk-QwLBsJV0iGURrRcdtTptNvFYvvw57CnwGBx0150DUuzoTGOA"
$base = "https://api.agnes-ai.cn/v1"

# read image -> base64 data uri
$bytes = [System.IO.File]::ReadAllBytes($img)
$b64 = [Convert]::ToBase64String($bytes)
$dataUri = "data:image/png;base64,$b64"
Write-Host ("Image bytes: {0}, base64 len: {1}" -f $bytes.Length, $b64.Length)

# ============ TEST 1: chat completions with data URI image (优化提示词 path) ============
Write-Host "`n===== TEST 1: agnes-2.5-flash chat with data-URI image ====="
$body1 = @{
  model = "agnes-2.5-flash"
  stream = $false
  messages = @(
    @{ role = "system"; content = "test" },
    @{ role = "user"; content = @(
      @{ type = "text"; text = "describe this image briefly" },
      @{ type = "image_url"; image_url = @{ url = $dataUri } }
    )}
  )
  max_tokens = 50
} | ConvertTo-Json -Depth 8

try {
  $resp = Invoke-WebRequest -Uri "$base/chat/completions" -Method Post -Headers @{ "Authorization" = "Bearer $key" } -ContentType "application/json" -Body $body1 -TimeoutSec 120
  Write-Host "STATUS: $($resp.StatusCode)"
  Write-Host ("RESP: " + $resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))
} catch {
  $r = $_.Exception.Response
  if ($r) {
    $sr = New-Object System.IO.StreamReader($r.GetResponseStream())
    $errBody = $sr.ReadToEnd()
    Write-Host "STATUS: $([int]$r.StatusCode)"
    Write-Host ("ERR BODY: " + $errBody.Substring(0, [Math]::Min(500, $errBody.Length)))
  } else {
    Write-Host "EXCEPTION: $($_.Exception.Message)"
  }
}

# ============ TEST 2: image generation with data URI image (图生图 path) ============
Write-Host "`n===== TEST 2: agnes-image-2.1-flash generation with 1 data-URI image ====="
$extra2 = @{ response_format = "url"; image = @($dataUri) }
$body2 = @{
  model = "agnes-image-2.1-flash"
  prompt = "keep the same subject, slight style change"
  size = "1K"
  ratio = "1:1"
  extra_body = $extra2
} | ConvertTo-Json -Depth 8

try {
  $resp = Invoke-WebRequest -Uri "$base/images/generations" -Method Post -Headers @{ "Authorization" = "Bearer $key" } -ContentType "application/json" -Body $body2 -TimeoutSec 180
  Write-Host "STATUS: $($resp.StatusCode)"
  Write-Host ("RESP: " + $resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))
} catch {
  $r = $_.Exception.Response
  if ($r) {
    $sr = New-Object System.IO.StreamReader($r.GetResponseStream())
    $errBody = $sr.ReadToEnd()
    Write-Host "STATUS: $([int]$r.StatusCode)"
    Write-Host ("ERR BODY: " + $errBody.Substring(0, [Math]::Min(500, $errBody.Length)))
  } else {
    Write-Host "EXCEPTION: $($_.Exception.Message)"
  }
}

# ============ TEST 3: image generation with TWO data URI images (多图融合 path) ============
Write-Host "`n===== TEST 3: agnes-image-2.1-flash generation with 2 data-URI images (多图融合) ====="
$extra3 = @{ response_format = "url"; image = @($dataUri, $dataUri) }
$body3 = @{
  model = "agnes-image-2.1-flash"
  prompt = "combine the two images into one scene"
  size = "1K"
  ratio = "1:1"
  extra_body = $extra3
} | ConvertTo-Json -Depth 8

try {
  $resp = Invoke-WebRequest -Uri "$base/images/generations" -Method Post -Headers @{ "Authorization" = "Bearer $key" } -ContentType "application/json" -Body $body3 -TimeoutSec 180
  Write-Host "STATUS: $($resp.StatusCode)"
  Write-Host ("RESP: " + $resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))
} catch {
  $r = $_.Exception.Response
  if ($r) {
    $sr = New-Object System.IO.StreamReader($r.GetResponseStream())
    $errBody = $sr.ReadToEnd()
    Write-Host "STATUS: $([int]$r.StatusCode)"
    Write-Host ("ERR BODY: " + $errBody.Substring(0, [Math]::Min(500, $errBody.Length)))
  } else {
    Write-Host "EXCEPTION: $($_.Exception.Message)"
  }
}
Write-Host "`nDONE"

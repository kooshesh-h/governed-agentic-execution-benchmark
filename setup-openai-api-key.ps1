$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$secretsDirectory = Join-Path $projectRoot '.secrets'
$secretPath = Join-Path $secretsDirectory 'openai_api_key'

if (Test-Path -LiteralPath $secretPath -PathType Leaf) {
    if ((Get-Item -LiteralPath $secretPath).Length -gt 0) {
        Write-Host 'A nonempty API key file already exists. It was not changed.'
        return
    }
}

if ([Console]::IsInputRedirected) {
    throw 'Interactive input is unavailable. Run this script in your own PowerShell terminal.'
}

New-Item -ItemType Directory -Path $secretsDirectory -Force | Out-Null
$secureKey = Read-Host 'Enter your existing OpenAI API key' -AsSecureString
if ($secureKey.Length -eq 0) {
    throw 'No API key was entered. The secret file was not written.'
}

$bstr = [IntPtr]::Zero
$characters = $null
$bytes = $null
try {
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    $characters = New-Object char[] $secureKey.Length
    for ($i = 0; $i -lt $characters.Length; $i++) {
        $characters[$i] = [char][Runtime.InteropServices.Marshal]::ReadInt16($bstr, 2 * $i)
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($characters)
    if ($bytes.Length -eq 0) {
        throw 'No API key was entered. The secret file was not written.'
    }
    [IO.File]::WriteAllBytes($secretPath, $bytes)
    Write-Host 'The local API key file was written successfully.'
}
finally {
    if ($bstr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
    if ($null -ne $characters) { [Array]::Clear($characters, 0, $characters.Length) }
    if ($null -ne $bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
    $secureKey.Dispose()
}

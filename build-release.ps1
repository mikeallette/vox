<#
  Builds the Vox release files for GitHub.

    .\build-release.ps1                 # installer + portable zip + checksums
    .\build-release.ps1 -NoModels       # app only (no speech model or voice-fingerprint model inside)

  Output (dist\release\):
    Vox-Setup-<version>.exe               per-user installer with the Parakeet model and the speaker models
    Vox-<version>-portable-win-x64.zip    the same app as a folder; unzip and run Vox.exe
    SHA256SUMS.txt                        hashes of everything above, plus the optional Accurate mode model

  Needs: the project-local .NET SDK (.dotnet), Inno Setup 7 (.tools\inno\ISCC.exe), and the models in .\models:
    models\ggml-parakeet-tdt-0.6b-v3-q8_0.bin          (ggml-org/parakeet-GGUF)
    models\speakers\nemo_en_titanet_small.onnx         (k2-fsa/sherpa-onnx speaker-recongition-models)
    models\asr-test\qwen3-1.7b\Qwen3-ASR-1.7B-Q4_K_M.gguf  (optional download, hashed only)

  The build runs on a clean copy of the source in %TEMP%, so it never touches src\Vox\bin or obj.
#>
param([switch]$NoModels)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_ROOT = Join-Path $root '.dotnet'
$env:PATH = "$($env:DOTNET_ROOT);$($env:PATH)"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1; $env:DOTNET_NOLOGO = 1
$version = ([xml](Get-Content "$root\src\Vox\Vox.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$iscc = Join-Path $root '.tools\inno\ISCC.exe'
if (-not (Test-Path $iscc)) { throw "Inno Setup not found: $iscc" }

$parakeet = Join-Path $root 'models\ggml-parakeet-tdt-0.6b-v3-q8_0.bin'
$titanet  = Join-Path $root 'models\speakers\nemo_en_titanet_small.onnx'
$qwen     = Join-Path $root 'models\asr-test\qwen3-1.7b\Qwen3-ASR-1.7B-Q4_K_M.gguf'
if (-not $NoModels) { foreach ($m in $parakeet, $titanet) { if (-not (Test-Path $m)) { throw "Model not found: $m" } } }

# 1. clean copy of the source
$work = Join-Path $env:TEMP 'vox-release-build'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$work\src" | Out-Null
& robocopy "$root\src\Vox" "$work\src\Vox" /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
& robocopy "$root\vendor" "$work\vendor" /E /NFL /NDL /NJH /NJS /NP | Out-Null

# 2. publish (self-contained: the target PC needs no .NET install)
$stage = Join-Path $work 'stage\app'
& dotnet publish "$work\src\Vox\Vox.csproj" -c Release -r win-x64 --self-contained true -o $stage | Out-Host
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
foreach ($rid in 'win-arm64', 'win-x86') { Remove-Item -Recurse -Force "$stage\runtimes\$rid" -ErrorAction SilentlyContinue }
Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force
Remove-Item -Force "$stage\models\*.bin" -ErrorAction SilentlyContinue
Copy-Item "$root\LICENSE" "$stage\LICENSE.txt" -Force
Copy-Item "$root\THIRD-PARTY-NOTICES.md" $stage -Force
Copy-Item "$root\packaging\README.txt" $stage -Force

# 3. models
if (-not $NoModels) {
    Copy-Item $parakeet "$stage\models" -Force
    New-Item -ItemType Directory -Force "$stage\models\speakers" | Out-Null
    Copy-Item $titanet "$stage\models\speakers" -Force
}

# 4. installer and portable zip
$out = Join-Path $root 'dist\release'
New-Item -ItemType Directory -Force $out | Out-Null
$setup = Join-Path $out "Vox-Setup-$version.exe"
$zip   = Join-Path $out "Vox-$version-portable-win-x64.zip"
Remove-Item $setup, $zip -ErrorAction SilentlyContinue
& $iscc /Q "/DAppVersion=$version" "/DStageDir=$stage" "/DOutDir=$out" "$root\packaging\Vox.iss"
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
# tar.exe (built into Windows 10+) writes standard forward-slash zip entries
& tar.exe -a -c -f $zip -C (Split-Path $stage) app
if ($LASTEXITCODE -ne 0) { throw "zip failed" }

# 5. checksums
$files = @($setup, $zip) + @(if (Test-Path $qwen) { $qwen })
$lines = foreach ($f in $files) { "{0}  {1}" -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $f -Leaf) }
Set-Content -Path "$out\SHA256SUMS.txt" -Value $lines -Encoding ascii
foreach ($f in $files) { "{0,-45} {1,8:0.0} MB" -f (Split-Path $f -Leaf), ((Get-Item $f).Length / 1MB) }
Remove-Item -Recurse -Force $work

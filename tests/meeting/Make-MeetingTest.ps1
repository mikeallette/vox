<#
Makes a synthetic meeting for testing Vox's meeting mode without anyone talking.
Each line of the script is spoken by a Windows voice into its own WAV; Vox.exe --meeting-test then places the
clips on a timeline (remote people on the "playback" track, You on the "microphone" track), runs the real meeting
pipeline, and grades the transcript and the speaker labels against this script.

  .\Make-MeetingTest.ps1 [-Out .\work]
Writes <Out>\clips\*.wav and <Out>\script.tsv (clip, track, person, text).
#>
param([string]$Out = (Join-Path $PSScriptRoot 'work'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force (Join-Path $Out 'clips') | Out-Null

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asTaskStream = $asTask.MakeGenericMethod([Windows.Media.SpeechSynthesis.SpeechSynthesisStream])
$rt = New-Object Windows.Media.SpeechSynthesis.SpeechSynthesizer
$rtVoices = @([Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices | Where-Object { $_.Language -like 'en*' })
Add-Type -AssemblyName System.Speech
$sapi = New-Object System.Speech.Synthesis.SpeechSynthesizer
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)

function RtVoice($name) { $v = $rtVoices | Where-Object { $_.DisplayName -like "*$name*" } | Select-Object -First 1; if (-not $v) { throw "voice $name missing" }; $v }
# People: three remote voices that really differ, and You (a pitched OneCore voice on the microphone)
$people = @{
    'Mark'  = @{ Engine = 'rt'; Voice = (RtVoice 'Mark');  Pitch = '+0%' }
    'Zira'  = @{ Engine = 'rt'; Voice = (RtVoice 'Zira');  Pitch = '+0%' }
    'David' = @{ Engine = 'sapi'; Voice = 'Microsoft David Desktop' }
    'You'   = @{ Engine = 'rt'; Voice = (RtVoice 'David'); Pitch = '-15%' }
}

$script = @(
    'Mark|Good morning everyone, thanks for joining. Let us start with the power supply status.'
    'Zira|Sure. The new flyback board came back from assembly on Tuesday and all twelve units powered up.'
    'Zira|Efficiency at full load is about eighty nine percent, which is a little below the target.'
    'You|Do we know where the loss is coming from?'
    'Zira|Most of it is in the clamp circuit. The leakage inductance is higher than we planned.'
    'David|I can rewind the transformer with interleaved windings. That should cut the leakage in half.'
    'Mark|How long would that take?'
    'David|About a week for the samples, and another week to test them.'
    'Mark|Okay, that works with the schedule. Zira, can you send David the measured waveforms?'
    'Zira|Yes, I will send them this afternoon.'
    'You|I will update the thermal model with the new loss numbers.'
    'Mark|Great. Next item is the connector shortage.'
    'David|The supplier says the twenty pin connectors will ship at the end of the month.'
    'Zira|Can we use the alternate part from the other vendor in the meantime?'
    'David|It has the same footprint, but the current rating is only three amps per pin.'
    'You|That is fine for the signal pins, but not for the power pins.'
    'Mark|Then let us split the order and use the alternate part only on the prototype boards.'
    'Zira|Agreed. I will update the bill of materials today.'
    'Mark|Last thing, the design review is moved to next Thursday at ten.'
    'David|Thursday works for me.'
    'Zira|Me too. I will book the conference room.'
    'Mark|Thanks everyone. See you Thursday.'
)

function Esc([string]$s) { [Security.SecurityElement]::Escape($s) }
$rows = New-Object System.Collections.Generic.List[string]
$rows.Add("clip`ttrack`tperson`ttext")
$n = 0
foreach ($line in $script) {
    $person, $text = $line -split '\|', 2
    $n++
    $p = $people[$person]
    $file = Join-Path $Out ('clips\{0:00}-{1}.wav' -f $n, $person)
    if ($p.Engine -eq 'rt') {
        $rt.Voice = $p.Voice
        $ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'><prosody pitch='$($p.Pitch)'>$(Esc $text)</prosody></speak>"
        $task = $asTaskStream.Invoke($null, @($rt.SynthesizeSsmlToStreamAsync($ssml)))
        [void]$task.Wait(-1)
        $src = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($task.Result)
        $dst = [IO.File]::Create($file)
        try { $src.CopyTo($dst) } finally { $dst.Dispose(); $src.Dispose(); $task.Result.Dispose() }
    } else {
        $sapi.SelectVoice($p.Voice)
        $sapi.SetOutputToWaveFile($file, $fmt); $sapi.Speak($text); $sapi.SetOutputToNull()
    }
    $track = if ($person -eq 'You') { 'mic' } else { 'playback' }
    $rows.Add("$([IO.Path]::GetFileName($file))`t$track`t$person`t$text")
}
[IO.File]::WriteAllLines((Join-Path $Out 'script.tsv'), $rows)
"wrote $n clips to $Out"

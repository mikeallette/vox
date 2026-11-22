VOX - private voice dictation
=============================

Vox types what you say into any app, at your cursor, and can turn a meeting into a Markdown
transcript with each speaker labeled. Everything runs on this PC. There is no account, no cloud,
no telemetry, and no internet connection is needed.

START
  Installed with Vox-Setup:  Start menu > Vox.
  Portable zip:              unzip anywhere, then double-click Vox.exe in the "app" folder.
  On first start, Vox asks where to save its data (history, dictionary, meeting transcripts).
  Pick a folder on this PC that is not synced to the cloud, for example C:\Users\<you>\Vox.

USE
  Hold Ctrl + Win and speak. Let go to insert the text where your cursor is.
  Hands-free: add Space while holding, or double-tap Ctrl + Win. Press again to finish.
  Esc cancels.  Alt + Shift + Z pastes your last dictation.  Alt + Shift + X copies it.
  Every shortcut can be changed in Settings > Shortcuts (click Change..., press the keys, let go).

MEETINGS
  Meetings tab > Start recording. Vox records your microphone ("You") and the sound your PC plays
  (Teams, Zoom, Google Meet, any app), then saves a timestamped .md transcript in
  <your data folder>\Meetings. With speaker labels on, other voices become Speaker 1, Speaker 2...
  and "Rename speakers..." puts real names in.

SPEECH MODELS
  Included: NVIDIA Parakeet TDT 0.6B v3 (q8_0). Fast and accurate on a normal laptop CPU.
  Optional Accurate mode: download Qwen3-ASR-1.7B-Q4_K_M.gguf (about 1.3 GB) from the Vox
  release page, then Settings > Speech model > "Import model file...". It is a little more
  accurate and several times slower. You can switch between models at any time.

DICTIONARY AND SNIPPETS
  Dictionary: teach Vox names and terms ("When I say" / "Write as", with "Heard as" for words
  it keeps mishearing). Snippets: say a short phrase, Vox types a longer block of text.

REMOVE
  Windows Settings > Apps > Vox > Uninstall (installer version), or delete the folder (portable).
  Your data folder is never deleted automatically.

TROUBLESHOOTING
  - Nothing is typed in an app: Settings > "Insert text by" > Typing.
  - Windows blocks the microphone: Windows Settings > Privacy > Microphone > allow desktop apps.
  - Apps running "as administrator" cannot receive text from a normal program (Windows rule).
  - Log file: %APPDATA%\Vox\vox.log (Settings > Open log file).

Source code, releases and license: https://github.com/mikeallette/vox (MIT License).
Third-party licenses: THIRD-PARTY-NOTICES.md in this folder.

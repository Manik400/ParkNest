' Runs keep-awake.ps1 with no window at all (powershell -WindowStyle Hidden still flashes one).
Option Explicit
Dim sh, fso, script
Set sh  = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
script = fso.GetParentFolderName(WScript.ScriptFullName) & "\keep-awake.ps1"
WScript.Quit sh.Run("powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & script & """", 0, True)

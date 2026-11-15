#Requires AutoHotkey v2.0
; Usage: AutoHotkey64.exe send-to-plateup.ahk <key> [holdMs]
;   key: AHK key name, e.g. Space, Enter, Escape, w, F7
;   holdMs: how long to hold the key (default 80). Unity's input system can miss
;           presses shorter than a frame.
; Sends only to the PlateUp window; exits with code 2 if the window isn't found.
key := A_Args.Length >= 1 ? A_Args[1] : "Space"
hold := A_Args.Length >= 2 ? Integer(A_Args[2]) : 80
if !WinExist("ahk_exe PlateUp.exe")
    ExitApp 2
WinActivate "ahk_exe PlateUp.exe"
if !WinWaitActive("ahk_exe PlateUp.exe", , 3)
    ExitApp 3
Sleep 150
SendEvent "{" key " down}"
Sleep hold
SendEvent "{" key " up}"
ExitApp 0

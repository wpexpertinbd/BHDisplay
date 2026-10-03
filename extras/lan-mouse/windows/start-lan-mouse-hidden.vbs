' Starts Lan Mouse's background service on Windows with NO window and NO console.
' Put a shortcut to this file in your Startup folder (Win+R -> shell:startup) to run it at every login.
' Expects lan-mouse.exe (and the DLLs from the same zip) in %LOCALAPPDATA%\Programs\lan-mouse\
' Settings come from %LOCALAPPDATA%\lan-mouse\config.toml (see config.example.toml next to this file).
Set sh = CreateObject("WScript.Shell")
exe = sh.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\Programs\lan-mouse\lan-mouse.exe"
sh.Run """" & exe & """ daemon", 0, False

@echo off
rem Ships next to Noodle.exe and goes on your PATH. A batch file waits for a windowed exe to finish,
rem so `noodle setup` can show the diff and ask before writing, like scripts/noodle on the Mac.
"%~dp0Noodle.exe" %*

@echo off
powershell -ExecutionPolicy ByPass -NoProfile -command "& """%~dp0common\Build.ps1""" -test /p:RunFlakyTests=true %*"
exit /b %ErrorLevel%
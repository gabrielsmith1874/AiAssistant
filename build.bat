@echo off
setlocal

pushd "%~dp0"
dotnet build AiAssistant.sln
set "EXIT_CODE=%ERRORLEVEL%"
popd

exit /b %EXIT_CODE%

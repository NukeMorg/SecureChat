@echo off
rem Остановка сервера и туннеля (данные в ./data сохраняются)
chcp 65001 >nul
cd /d "%~dp0"
docker compose down
pause

@echo off
rem SecureChat: сервер + Cloudflare Tunnel одной командой (двойной клик тоже работает)
chcp 65001 >nul
cd /d "%~dp0"

if not exist .env (
    copy .env.example .env >nul
    echo.
    echo [!] Файл .env создан из шаблона. Заполните CLOUDFLARE_TUNNEL_TOKEN ^(и JWT_SECRET^), затем запустите снова.
    echo.
    pause
    exit /b 1
)

echo Запуск сервера и туннеля...
docker compose up -d --build
if errorlevel 1 (
    echo.
    echo [!] Ошибка запуска docker compose — проверьте, что Docker Desktop работает.
    pause
    exit /b 1
)

echo.
echo Статус:
docker compose ps
echo.
echo Локально:  http://localhost:9000/health
echo Извне:     домен из Public Hostname туннеля (см. дашборд Cloudflare)
echo Остановить: stop.bat
pause

@echo off
chcp 65001 >nul
cd /d "%~dp0"
python gui.py
if errorlevel 1 (
    echo.
    echo [오류] 실행에 실패했습니다. Python 및 필수 패키지 설치 여부를 확인하세요:
    echo   pip install -r requirements.txt
    pause
)

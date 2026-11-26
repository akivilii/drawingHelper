# Minecraft Drawing Helper

마인크래프트 아이템 팔레트(`minecraft_palette_rgb.md`, 61개 아이템 × 4단계 명암 = 244색)를 기반으로 이미지를 원하는 픽셀 크기로 축소하고, 인간 시각 인지 기반 색차 알고리즘(**CIEDE2000**)으로 가장 가까운 아이템 색상으로 변환하는 도구입니다.

## ZIP 다운로드

- **[최신 릴리즈 ZIP 다운로드 (v0.1.0)](https://github.com/akivilii/drawingHelper/releases/latest/download/drawingHelper-v0.1.0.zip)**
- **[최신 main 브랜치 소스 ZIP 다운로드](https://github.com/akivilii/drawingHelper/archive/refs/heads/main.zip)**
- **[전체 Releases 페이지 보기](https://github.com/akivilii/drawingHelper/releases)**

## 빠른 시작 (Windows)

1. 위 링크에서 ZIP 파일을 다운로드하고 압축을 풉니다.
2. Python 3.10+ 환경에서 필수 패키지를 설치합니다:
   ```powershell
   pip install -r requirements.txt
   ```
3. 테스트 UI를 실행합니다 (`run_gui.bat` 더블클릭 또는 아래 명령 실행):
   ```powershell
   python gui.py
   ```

## 주요 기능

- **인지 기반 색상 매칭 (`drawing_helper/`)**:
  - **CIEDE2000 ($\Delta E_{00}$)** 기본 탑재 (OKLab, CIE76, Redmean, RGB 비교 지원)
  - 선형광(Linear-light) 감마 보정 리사이징 (`fit` / `stretch` / `cover`)
  - 선택적 디더링 (`none`, `bayer4`, `bayer8`, `floyd_steinberg`)
- **테스트용 파이썬 UI (`gui.py`)**:
  - 이미지 삽입 / 크기·알고리즘 선택 / 변환 / 결과 저장
  - **32×32 칸 분할 및 확대 보기** (클릭 확대, 방향키 이동, 1px/8px 격자, 1~32 좌표 눈금)
  - 마우스 호버 시 픽셀별 아이템명·명암 단계·HEX/RGB·$\Delta E_{00}$ 실시간 표시
  - 전체 / 현재 32×32 칸별 필요 아이템 목록 확인

## CLI 사용 예시

```powershell
# 단일 색상과 가장 가까운 아이템 탐색
python main.py lookup "#E8B89A" --all-metrics --top 3

# 이미지 변환 + 설계도 JSON 저장
python main.py convert input.png -o output.png --width 64 --height 64 --scale 8 --grid --json-out blueprint.json

# 5가지 색상 매칭 알고리즘 시각 비교 시트 생성
python main.py compare -o comparison_chart.png --scale 3
```

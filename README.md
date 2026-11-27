# Minecraft Drawing Helper

마인크래프트 아이템 팔레트(`minecraft_palette_rgb.md`, 61개 아이템 × 4단계 명암 = 244색)를 기반으로 이미지를 원하는 픽셀 크기로 축소하고, 인간 시각 인지 기반 색차 알고리즘(**CIEDE2000**)으로 가장 가까운 아이템 색상으로 변환하는 Windows 데스크톱 앱입니다.

## 다운로드 (Windows)

- **[DrawingHelperSetup.exe 다운로드 (v1.0.2)](https://github.com/akivilii/drawingHelper/releases/latest/download/DrawingHelperSetup.exe)**
- **[SHA256SUMS.txt](https://github.com/akivilii/drawingHelper/releases/latest/download/SHA256SUMS.txt)** · **[전체 Releases 보기](https://github.com/akivilii/drawingHelper/releases)**

> 설치관리자 실행 후 설치 경로와 **바탕화면 바로가기 만들기** 옵션을 선택해 간편하게 설치할 수 있으며, Windows **앱 및 기능**에서 깔끔하게 제거할 수 있습니다. (요구 사항: Windows 10/11, .NET 10 Desktop Runtime)

### 다운로드 / 실행 시 보안 경고가 뜨는 경우

개인 개발 오픈소스 프로그램은 유료 공인 코드 서명 인증서가 없어, Chrome·Edge에서 **“일반적으로 다운로드되지 않음 / 의심스러운 다운로드”** 또는 Windows에서 **“Windows의 PC 보호 (SmartScreen)”** 경고가 표시될 수 있습니다.

- **브라우저 경고**: 우측 메뉴(`⋯`) → **유지** → **그래도 계속**
- **Windows SmartScreen**: **추가 정보** → **실행**
- **무결성 검증 (SHA-256)**:
  ```powershell
  Get-FileHash DrawingHelperSetup.exe -Algorithm SHA256
  # v1.0.2: 5B7BE0C6132E0E78611A93792C4C9F5D20615B3CB3EADD68C66E384E3A16F28A
  ```
- 소스가 전체 공개되어 있으므로 원하시면 아래 명령으로 직접 빌드해서 사용하실 수도 있습니다.

## 주요 기능

- **인지 기반 색상 매칭 (`DrawingHelper.Core`)**:
  - 분기한정(Branch-and-Bound) 최적화 **CIEDE2000 ($\Delta E_{00}$)** 기본 탑재 (OKLab, CIE76, Redmean, RGB 지원)
  - 감마 보정 선형광(Linear-light) 리사이징 (`Fit` / `Stretch` / `Cover`)
  - 선택적 디더링 (`None`, `Bayer4`, `Bayer8`, `FloydSteinberg`)
  - 61종 아이템 제외 필터 (보유하지 않은 아이템 제외 후 즉시 재매칭)
- **스튜디오 워크스페이스 (`DrawingHelper.App`)**:
  - **32×32 칸 분할·확대 보기** 및 우측 **네비게이터 미니맵** 클릭 이동
  - **배경 유지 외곽선 하이라이트** (`Yellow` / `Cyan`, 단계별 / 아이템 전체)
  - 마우스 휠 · `Ctrl + +/-/0` 확대/축소, 드래그 패닝, `1~32` 고정 좌표 헤더
  - 칸/색상별 완료 체크리스트 및 진행 상태 JSON 저장·불러오기
  - 스플릿 뷰 크기 전환, 항상 위 고정(`Topmost`), 창 투명도, 전역 단축키(`Ctrl+Alt+방향키 / Q,E`), `F11` 전체화면

## 소스에서 빌드

```powershell
# 핵심 엔진 단위 테스트 실행
dotnet run --project csharp\DrawingHelper.Core.Tests\DrawingHelper.Core.Tests.csproj -c Release

# 설치관리자(DrawingHelperSetup.exe) 빌드
powershell -ExecutionPolicy Bypass -File csharp\build_installer.ps1
```

# PET THEM! Balance Lab

**PET THEM!** 게임의 밸런스를 측정하고 설명하는 개발 도구입니다.
MCP 서버, 봇 시뮬레이터, 기록 분석, 그리고 나중에 추가할 `Create_Balance_Report` HTML 생성기를 담습니다.

게임 저장소: [jjjGi/pet-them](https://github.com/jjjGi/pet-them) (비공개)

## 게임 저장소가 필요합니다

전투 규칙과 밸런스 설정의 원본은 게임 저장소에 있습니다. 이 저장소는 **복사하지 않고 참조**합니다.
따라서 두 저장소를 나란히 두거나 경로를 알려줘야 빌드됩니다.

~~~text
D:/Project/
  PetThemGame/          <- jjjGi/pet-them
  PetThemBalanceLab/    <- 이 저장소
~~~

다른 위치에 뒀다면 환경 변수로 알려줍니다.

~~~powershell
$env:PETTHEM_GAME_ROOT = 'D:/somewhere/PetThemGame'
~~~

경로가 틀리면 빌드가 이유를 말하며 멈춥니다. 조용히 빈 값으로 진행하지 않습니다.

## 현재 구현

| 기능 | 상태 |
| --- | --- |
| MCP stdio 서버 | 동작 |
| `get_lab_status` / `get_balance_config` / `list_bot_policies` / `run_simulation` / `read_run_log` | 동작 |
| 봇 시뮬레이터 CLI | 동작 |
| 실행 기록(JSONL) 읽기와 불완전 기록 판별 | 동작 |
| `compare_experiments` / `analyze_playtests` / `create_balance_candidate` | 미구현 |
| `Create_Balance_Report` HTML 보고서 | 미구현 |

실제 사람의 플레이 기록은 아직 하나도 없습니다. 그래서 플레이어 분석은 지금 불가능합니다.

## 검증

.NET SDK 10.0.400 이상 같은 패치 계열이 필요합니다.

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check.ps1
~~~

검사 9개에는 MCP 서버를 실제 자식 프로세스로 띄워 `initialize` → `tools/list` → `tools/call`까지 주고받는 통신 검사가 포함됩니다.
빌드가 되는지만 보는 검사가 아닙니다.

## 시뮬레이터 실행

~~~powershell
dotnet run --project src/Simulator -c Release -- --runs 5 --seed 42
dotnet run --project src/Simulator -c Release -- --policy still-auto-punch-v1 --runs 5 --seed 42
dotnet run --project src/Simulator -c Release -- --help
~~~

결과 JSONL과 요약 JSON은 `experiments/` 아래에 저장되며 Git에서 제외됩니다.
`--output`은 `experiments/` 바깥을 가리키면 거부됩니다.

### 봇 정책

| ID | 행동 |
| --- | --- |
| `orbit-auto-punch-v1` | 경기장 중앙을 크게 돌며 매 프레임 자동 조준 펀치 |
| `still-auto-punch-v1` | 제자리에서 자동 조준 펀치만 반복 |
| `flee-auto-punch-v1` | 가장 가까운 적의 반대 방향으로 이동하며 자동 조준 펀치 |

**이 봇들은 재현 가능한 점검 도구입니다.** 사람의 반응 속도·조준·피로를 흉내 내지 않으므로,
봇의 승률을 플레이어의 승률로 읽으면 안 됩니다.

## MCP 서버 연결

~~~powershell
dotnet build src/McpServer -c Release
~~~

AI 클라이언트의 MCP 설정에 아래처럼 등록합니다. 로컬 stdio 연결이며 네트워크 포트를 열지 않습니다.

~~~json
{
  "mcpServers": {
    "pet-them-balance-lab": {
      "command": "D:/Project/PetThemBalanceLab/src/McpServer/bin/Release/net10.0/PetThem.BalanceLab.Mcp.exe",
      "env": { "PETTHEM_GAME_ROOT": "D:/Project/PetThemGame" }
    }
  }
}
~~~

도구별 입력·출력은 [docs/mcp-tools.md](docs/mcp-tools.md)에 있습니다.

## 측정 원칙

- 수치는 코드가 계산합니다. AI가 숫자를 만들어내지 않습니다.
- 관찰된 결과, 원인 가설, 수정 제안을 구분해 기록합니다.
- 데이터가 없으면 없다고 말합니다.
- `run_end`가 없는 기록은 **불완전**이며 사망으로 해석하지 않습니다.
- 봇 실험과 사람의 플레이 기록을 합쳐서 통계를 내지 않습니다.

## 이어서 개발하기

- 기획과 단계별 완료 기준: 게임 저장소의 `PROJECT.md` 8·9절.
- 작업 지침: [AGENTS.md](AGENTS.md)
- 검증 기록: [docs/verification.md](docs/verification.md)

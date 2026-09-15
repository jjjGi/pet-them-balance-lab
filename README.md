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
| 설정 조회 · 봇 정책 · 반복 실험 · 기록 읽기 | 동작 |
| 실험 목록과 **시드끼리 짝지은** 비교 | 동작 |
| 밸런스 후보 저장 (게임 설정은 건드리지 않음) | 동작 |
| `Create_Balance_Report` 단일 파일 HTML 보고서 | 동작 |
| `analyze_playtests` | 미구현 |

MCP 도구 10개가 동작하고 `analyze_playtests` 하나가 남았습니다.
남은 이유는 기술이 아니라 데이터입니다. **실제 사람의 플레이 기록이 아직 0건**이라 읽을 대상이 없습니다.

도구별 입력·출력과 한도는 [docs/mcp-tools.md](docs/mcp-tools.md)에 있습니다.

## 검증

.NET SDK 10.0.400 이상 같은 패치 계열이 필요합니다.

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check.ps1
~~~

검사 14개에는 이런 것들이 들어 있습니다.

- MCP 서버를 실제 자식 프로세스로 띄워 `initialize` → `tools/list` → `tools/call`까지 주고받는 통신 검사.
  빌드가 되는지만 보는 검사가 아닙니다.
- `get_lab_status`가 `tools/list`에 없는 도구를 구현됐다고 주장하면 실패.
- 보고서가 외부 리소스를 전혀 참조하지 않고, 데이터에서 온 문자열을 이스케이프하는지.
- 비교가 정말 시드끼리 짝지어 계산하는지, 비교 불가능한 조합을 거부하는지.
- 밸런스 설정이 아닌 JSON을 조용히 기본값으로 읽지 않는지.

## MCP로 한 바퀴 돌려보기

~~~powershell
dotnet build src/McpServer -c Release

./scripts/mcp-call.ps1 run_simulation '{"runs":5,"seed":42,"label":"기준","outputDirectory":"demo"}'
./scripts/mcp-call.ps1 list_experiments '{"limit":5}'
./scripts/mcp-call.ps1 create_balance_candidate '{"changes":"{\"punchRange\":1.8}","rationale":"왜 바꾸는지"}'
./scripts/mcp-call.ps1 compare_experiments '{"baseline":"exp-...","candidate":"exp-..."}'
./scripts/mcp-call.ps1 Create_Balance_Report '{"baselineExperimentId":"exp-...","outputPath":"balance-01.html"}'
~~~

보고서는 `reports/` 아래에 생기고 브라우저로 그냥 열면 됩니다. 서버도 인터넷도 필요 없습니다.

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

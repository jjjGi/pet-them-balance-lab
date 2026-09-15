# MCP 도구 v0.1

서버 이름 `pet-them-balance-lab`, 로컬 stdio 전용입니다. 네트워크 포트를 열지 않습니다.
모든 도구는 JSON 문자열 하나를 돌려줍니다. 처리할 수 없는 입력은 `{ "error", "message", "hint" }` 형태로 답합니다.

## 구현된 도구

### get_lab_status

입력 없음.

어떤 도구가 실제로 구현됐는지, 게임 저장소 경로가 무엇인지, 빠진 경로가 있는지 보고합니다.
**다른 도구를 쓰기 전에 이걸 먼저 호출하면 없는 기능을 있다고 말하는 일을 막을 수 있습니다.**

주요 출력: `gameRepositoryRoot`, `labRoot`, `defaultConfigPath`, `experimentsRoot`, `missingPaths`, `implemented`, `notImplementedYet`, `notes`.

### get_balance_config

| 입력 | 형 | 기본값 | 의미 |
| --- | --- | --- | --- |
| `configPath` | string? | 게임 저장소의 `balance-default.json` | 읽을 설정 파일 |

출력: `path`, `version`, `owner`, `config`(모든 밸런스 값).
이 도구는 읽기 전용입니다. 설정 파일을 수정하지 않습니다.

### list_bot_policies

입력 없음. 사용할 수 있는 봇 정책 ID와 설명, 그리고 봇이 측정하지 **못하는** 것을 함께 돌려줍니다.

### run_simulation

| 입력 | 형 | 기본값 | 허용 범위 |
| --- | --- | --- | --- |
| `runs` | int | 5 | 1 – 200 |
| `seed` | int | 42 | 실행 i는 `seed + i` 사용 |
| `policy` | string? | `orbit-auto-punch-v1` | `list_bot_policies`의 ID |
| `configPath` | string? | 기본 설정 | 존재하는 JSON 파일 |
| `outputDirectory` | string? | `mcp` | `experiments/` 안으로 제한 |
| `writeLogs` | bool | true | false면 파일을 만들지 않음 |

전투 코어를 60 Hz 고정 스텝으로 돌리고, 실행마다 JSONL 로그 한 개와 요약 JSON 한 개를 씁니다.

출력에는 실행별 결과(`seed`, `state`, `seconds`, `kills`, `health`, `damageTaken`, `enemiesSpawned`, `log`)와
그 결과에서 코드로 계산한 `aggregates`(`winRate`, `meanSeconds`, `meanKills`, `minKills`, `maxKills`,
`meanHealthRemaining`, `meanDamageTaken`, `runsWithoutDamage`)가 들어갑니다.
`limitation` 문장도 항상 함께 나옵니다. 결과를 인용할 때 이 문장을 빼지 않습니다.

비용 한도: `runs` 최대 200, 설정의 `duration` 최대 3600초. 한도를 넘으면 실행 전에 거부합니다.

### read_run_log

| 입력 | 형 | 의미 |
| --- | --- | --- |
| `path` | string | 시뮬레이터나 게임이 남긴 `.jsonl` 기록 |

파일에 **실제로 들어 있는 것**을 셉니다: 이벤트 종류별 개수, `kill` 이벤트 수, 누적 피해량, 종료 사유.

- `run_end`가 없으면 `complete: false`이고 경고를 남깁니다. **사망으로 해석하지 않습니다.**
- `run_end`가 보고한 처치 수와 `kill` 이벤트 수가 다르면 경고합니다.
- JSON이 깨진 줄은 건너뛰고 몇 번째 줄인지 남깁니다.
- 64 MB보다 큰 파일은 읽지 않고 거부합니다.

로그 필드의 의미는 게임 저장소의 `docs/telemetry.md`에 있습니다.

## 아직 구현하지 않은 도구

`get_lab_status`의 `notImplementedYet`과 이 목록을 항상 일치시킵니다.

| 도구 | 필요한 선행 작업 |
| --- | --- |
| `compare_experiments` | 실험 저장·조회 구조 |
| `analyze_playtests` | 사람의 플레이 기록. 현재 0건 |
| `create_balance_candidate` | 설정 후보 버전 관리 |
| `Create_Balance_Report` | 위 세 가지와 HTML 템플릿 |

## 안전 관련 결정

- 클라이언트가 보낸 출력 경로는 `PathGuard`가 절대 경로로 정규화한 뒤 `experiments/` 안인지 확인합니다. 바깥이면 거부합니다.
- 입력 파일 경로는 존재 여부를 확인하고, 없으면 만들지 않고 거부합니다.
- 서버는 stdout에 MCP 메시지만 씁니다. 로그는 전부 stderr로 보냅니다.
- 도구는 게임 저장소의 파일을 읽기만 합니다. 밸런스 설정을 덮어쓰지 않습니다.

## 클라이언트 등록 예시

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

`PETTHEM_GAME_ROOT`를 생략하면 빌드 시점에 기록된 경로를 사용합니다.

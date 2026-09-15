# MCP 도구 v0.2

서버 이름 `pet-them-balance-lab`, 로컬 stdio 전용입니다. 네트워크 포트를 열지 않습니다.
모든 도구는 JSON 문자열 하나를 돌려줍니다. 처리할 수 없는 입력은 `{ "error", "message", "hint" }` 형태로 답합니다.

| 도구 | 상태 |
| --- | --- |
| `get_lab_status` | 구현 |
| `get_balance_config` | 구현 |
| `list_bot_policies` | 구현 |
| `run_simulation` | 구현 |
| `read_run_log` | 구현 |
| `list_experiments` | 구현 |
| `compare_experiments` | 구현 |
| `create_balance_candidate` | 구현 |
| `list_balance_candidates` | 구현 |
| `Create_Balance_Report` | 구현 |
| `analyze_playtests` | **미구현** — 사람의 플레이 기록이 0건이라 읽을 대상이 없습니다 |

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
| `label` | string? | 없음 | 왜 돌렸는지 한 줄. 비교와 보고서에 표시됨 |

각 실행에는 `experimentId`가 붙습니다. `compare_experiments`와 `Create_Balance_Report`가 이 ID로 실험을 찾습니다.
ID 조회는 `experiments/` 아래만 검색합니다.

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
검사에서 `tools/list`에 없는 도구를 `implemented`가 주장하면 실패합니다.

| 도구 | 막고 있는 것 |
| --- | --- |
| `analyze_playtests` | 사람의 플레이 기록이 필요합니다. 현재 0건이라 구현해도 실행할 대상이 없습니다 |

## 안전 관련 결정

- 클라이언트가 보낸 출력 경로는 `PathGuard`가 절대 경로로 정규화한 뒤 허용된 루트(`experiments/`, `reports/`) 안인지 확인합니다. 바깥이면 거부합니다.
- 입력 파일 경로는 존재 여부를 확인하고, 없으면 만들지 않고 거부합니다.
- 서버는 stdout에 MCP 메시지만 씁니다. 로그는 전부 stderr로 보냅니다.
- 도구는 게임 저장소의 파일을 읽기만 합니다. 밸런스 설정을 덮어쓰지 않습니다.
- 밸런스 설정으로 보이지 않는 JSON 파일은 거부합니다. 조용히 기본값으로 실행하지 않습니다.
- 보고서에 들어가는 문자열은 모두 이스케이프합니다. 보고서를 여는 것만으로는 아무것도 실행되지 않습니다.

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

---

## v0.2에서 추가된 도구


### list_experiments

| 입력 | 형 | 기본값 |
| --- | --- | --- |
| `limit` | int | 25 |

저장된 실험을 최신순으로 나열합니다. `experimentId`, 정책, 설정 버전, 실행 수, 승률, 평균 처치, 평균 피해, 파일 경로.

### compare_experiments

| 입력 | 형 | 의미 |
| --- | --- | --- |
| `baseline` | string | 기준 실험 ID 또는 요약 파일 경로 |
| `candidate` | string | 비교 대상 실험 ID 또는 경로 |

**시드끼리 짝지어** 비교합니다. 같은 시드는 같은 적 등장을 만들므로, 차이가 운이 아니라 설정 변경에서 온 것이 됩니다.

다음 경우에는 `comparable: false`로 답하고 숫자를 내놓지 않습니다.

- 봇 정책이 다름 — 정책만으로도 수치가 움직여 설정 효과를 분리할 수 없음
- 실행 길이가 다름 — 처치 수와 피해량을 비교할 수 없음
- 공유 시드가 없음
- 같은 실험을 양쪽에 지정

지표별로 `baselineMean`, `candidateMean`, `meanDelta`, `minDelta`, `maxDelta`,
그리고 `seedsIncreased / seedsDecreased / seedsUnchanged`를 돌려줍니다.

**증가·감소는 좋고 나쁨이 아닙니다.** 받은 피해가 늘어난 것이 나쁜지 의도한 것인지는 설계 판단이며 도구가 정하지 않습니다.

### create_balance_candidate

| 입력 | 형 | 의미 |
| --- | --- | --- |
| `changes` | string | `{"punchRange": 2.0, "gruntSpeed": 1.6}` 형태의 JSON 객체 |
| `rationale` | string | 왜 이 변경을 제안하는지. 비어 있으면 거부 |
| `baseConfigPath` | string? | 기본값은 게임의 balance-default.json |
| `candidateId` | string? | 영숫자·`-`·`_`만, 64자 이하 |

**게임의 밸런스 설정을 수정하지 않습니다.** `candidates/<id>.json`에 새 파일로 저장합니다.
게임에 반영하는 것은 사람이 따로 하는 별개의 작업입니다.

저장 전에 전투 규칙으로 검증합니다. 쿨다운 0 같은 값은 실행 시점이 아니라 저장 시점에 거부됩니다.
알 수 없는 필드 이름, 무한/NaN, 정수 필드에 소수를 넣는 것도 거부합니다.
같은 ID가 이미 있으면 덮어쓰지 않고 거부합니다.

후보 파일은 `run_simulation`의 `configPath`에 그대로 넘길 수 있습니다. 중첩된 `config`를 알아서 꺼냅니다.

### list_balance_candidates

저장된 후보와 변경 내역·근거, 그리고 수정 가능한 필드 목록을 돌려줍니다.

### Create_Balance_Report

| 입력 | 형 | 의미 |
| --- | --- | --- |
| `baselineExperimentId` | string | 기준 실험 ID |
| `title` | string? | 보고서 제목 |
| `candidateExperimentIds` | string? | 쉼표로 구분한 비교 대상 실험 ID |
| `question` | string? | 이 보고서가 답해야 할 질문 |
| `outputPath` | string? | `reports/` 아래 파일 이름 |

**단일 HTML 파일**을 만듭니다. 외부 CDN, 폰트, 스크립트, 서버가 전혀 없습니다. 인터넷 없이 열립니다.

구성:

| 영역 | 내용 |
| --- | --- |
| 핵심 요약 | 기록된 수치에서 계산한 문장. 원인 가설 아님 |
| 이 보고서가 답할 수 없는 것 | 요약 바로 다음에 배치. 없는 데이터를 명시 |
| 사용한 데이터 | 실험 ID, 정책, 설정 버전, 시드 목록 |
| 실행별 결과 | 시드마다의 원본 수치 |
| 시간에 따른 변화 | 살아 있는 적 수 / 체력 / 누적 처치. 인라인 SVG |
| 비교 실험 | 설정 차이와 시드 짝지은 변화량 |
| 기준 설정 값 | 규칙 데이터 |

차트는 1초마다 기록된 스냅샷만 사용하고 그 사이를 추정하지 않습니다.
선은 색뿐 아니라 점선 패턴과 범례로도 구분되어 흑백 인쇄와 색각 차이에서도 읽힙니다.

기록에서 온 문자열은 모두 이스케이프합니다. 출력 경로는 `reports/` 밖이면 거부합니다.

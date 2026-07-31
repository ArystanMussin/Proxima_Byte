# PGW — Protocol Gateway

Modbus TCP/RTU + OPC UA → единое пространство тегов → Modbus TCP Server. Реализация v0.1 по
`TZ_protocol_gateway_v0_1.md`.

```
Modbus TCP Client  ──┐                      ┌── Modbus TCP Server
Modbus RTU Client  ──┼──▶ Tag Space (core) ─┤
OPC UA Client      ──┘   (value+quality     └── (REST /runtime, /config)
                          +timestamp)
```

Ядро (`PGW.Core`) не содержит ни строчки, специфичной для Modbus или OPC UA — оно работает только через
контракты `IProtocolDriver`/`IProtocolInterface`. Это подтверждено тестом `CoreIsolationTests`
(фейковый драйвер третьего "протокола" проходит через реальный Modbus TCP Server без единой правки в
ядре или интерфейсе).

## Структура

```
src/PGW.Core            — Tag Space, контракты, конфиг, YAML-загрузчик, cross-platform пути
src/PGW.Drivers.Modbus   — Modbus TCP/RTU Client (master, общая логика опроса на общем
                           ModbusClientDriverBase — FluentModbus.ModbusClient — разное только открытие
                           соединения) + Modbus TCP Server (slave)
src/PGW.Drivers.OpcUa    — OPC UA Client
src/PGW.Simulator        — встроенные симулированные Modbus TCP / OPC UA серверы (`pgw simulate`,
                           см. "Быстрый просмотр" ниже) — тестировать без реального железа
src/PGW.Host             — composition root: DI-сборка драйверов/интерфейсов, REST API, Worker Service,
                           wwwroot/ — веб-панель (см. "Веб-панель" ниже)
tests/PGW.Core.Tests     — unit- и end-to-end тесты (реальные TCP-сокеты и реальный OPC UA сервер,
                           без моков протокола)
deploy/                  — systemd unit, Dockerfile
project.sample.yaml      — пример конфигурации (§7.2)
```

## Сборка и запуск

Нужен .NET 8 SDK.

```bash
dotnet build PGW.sln

cp project.sample.yaml /etc/pgw/project.yaml   # или PGW_CONFIG=/path/to/project.yaml
dotnet run --project src/PGW.Host

# CLI
dotnet run --project src/PGW.Host -- validate      # проверить конфиг и выйти
dotnet run --project src/PGW.Host -- export-map     # выгрузить карту регистров в CSV
dotnet run --project src/PGW.Host -- simulate       # поднять встроенные Modbus/OPC UA симуляторы (см. ниже)
```

## Быстрый просмотр без реального оборудования

`pgw simulate` поднимает в том же исполняемом файле **и** Modbus TCP Server, **и** OPC UA Server с
несколькими "дышащими" тегами (T1_supply/P1_supply меняются каждые ~0.7 с, setpoint — RW) — не
отдельный скрипт-заглушка, а такой же встроенный режим PGW, как `run`/`validate`. `demo/project.yaml`
настраивает оба протокола сразу на этот же симулятор, так что один прогон показывает полный
кросс-протокольный путь (Modbus-источник и OPC UA-источник → общий Modbus-выход). Два терминала:

```bash
# терминал 1 — встроенный симулятор: Modbus на 127.0.0.1:15020, OPC UA на opc.tcp://127.0.0.1:4841/pgw/simulator
dotnet run --project src/PGW.Host -- simulate

# терминал 2 — сам шлюз с демо-конфигом (оба источника указывают на симулятор из терминала 1)
cd src/PGW.Host
PGW_CONFIG=../../demo/project.yaml dotnet run          # Windows PowerShell: $env:PGW_CONFIG="..\..\demo\project.yaml"; dotnet run
```

Порты симулятора переопределяются `PGW_SIM_MODBUS_PORT`/`PGW_SIM_OPCUA_PORT`, если 15020/4841 заняты
(4841, а не стандартный 4840 OPC UA — чтобы не конфликтовать с другим OPC UA софтом на той же машине).
Сертификат OPC UA-симулятора генерируется один раз при первом запуске (несколько секунд) и кэшируется
в `<DataDir>/certs/_simulator`.

Открой `http://localhost:8420/` — увидишь оба источника (`ctp_12` по Modbus, `plc_opcua` по OPC UA)
подключёнными, с живыми значениями по обоим протоколам одновременно.

Порядок запуска не важен: если шлюз стартовал раньше симулятора (или устройство временно недоступно),
оба драйвера переподключаются сами с экспоненциальной паузой — источник поднимется, как только сервер
станет доступен.

### Подключение SCADA / ModScan32 к выходу шлюза

Выход `scada_slave` из `demo/project.yaml` — это Modbus TCP Server на `127.0.0.1:15021` (не 502 и не
порт панели). Адреса ниже 0-based, как в поле Address у ModScan32:

| Тег | Unit Id | Point Type | Address | Length |
|---|---|---|---|---|
| `ctp_12.T1_supply` | 1 | 03 Holding Register | 0 | 1 |
| `ctp_12.P1_supply` | 1 | 03 Holding Register | 2 | 1 |
| `ctp_12.setpoint` (RW) | 1 | 03 Holding Register | 4 | 1 |
| `ctp_12.pump1_run` | 1 | 02 Input Status | 0 | 1 |
| `plc_opcua.T1_supply` | 2 | 03 Holding Register | 0 | 4 |
| `plc_opcua.P1_supply` | 2 | 03 Holding Register | 4 | 4 |
| `plc_opcua.setpoint` (RW) | 2 | 03 Holding Register | 8 | 4 |
| `plc_opcua.pump1_run` | 2 | 02 Input Status | 0 | 1 |

Теги юнита 2 — `float64`, то есть 4 регистра на значение; чтобы клиент показывал одно число, а не
четыре, выбери в нём формат Double (порядок слов по умолчанию ABCD, старший регистр первый).

Тесты (включают реальный Modbus round-trip через loopback-сокеты, не моки):

```bash
dotnet test PGW.sln
```

## Конфигурация

Один YAML-файл (`gateway` / `sources` / `outputs`), формат — см. `project.sample.yaml` и §7.2 ТЗ.
Путь по умолчанию: `/etc/pgw/project.yaml` (Linux) или `%ProgramData%\PGW\config\project.yaml`
(Windows); переопределяется переменной `PGW_CONFIG`.

Пароли — только через `password_env: ИМЯ_ПЕРЕМЕННОЙ` в конфиге OPC UA `security:`, не текстом в YAML.

### Modbus RTU (RS-485/serial) — `driver: modbus_rtu_client`

Тот же протокол Modbus, что и `modbus_tcp_client` (те же `area`/`address`/`type` в тегах, тот же
`ModbusCodec`, тот же общий цикл опроса — `ModbusClientDriverBase` работает через абстрактный
`FluentModbus.ModbusClient`, у которого что TCP-, что RTU-клиент реализуют все методы чтения/записи
одинаково), просто вместо TCP-сокета — последовательный порт. Это то, чем на практике говорит
подавляющее большинство счётчиков и контроллеров на объекте — TCP там, где стоит современный
Ethernet-шлюз/ПЛК, RTU — почти везде ещё.

```yaml
sources:
  - name: energy_meter_1
    driver: modbus_rtu_client
    serial_port: /dev/ttyUSB0      # Windows: COM3 и т.п. — не "port" (см. ниже, почему)
    baud_rate: 9600
    parity: even                   # even (по умолчанию) | odd | none
    stop_bits: one                 # one (по умолчанию) | two
    unit_id: 3                     # адрес устройства на шине RS-485
    scan_rate_ms: 2000             # RS-485 — общая полудуплексная шина, обычно опрашивают реже TCP
    tags:
      - {name: active_energy, area: HR, address: 0, type: float32, units: "kWh"}
```

Поле называется `serial_port`, а не `port` — потому что `port` уже занято числовым TCP-портом у
`modbus_tcp_client`, а форма источника в панели показывает поля обеих схем сразу (см. "Веб-панель"
ниже); одно имя поля под строку "COM3" и под число 502 сразу не разъехалось бы. `inter_request_delay_ms`
по умолчанию 20 (не 0, как у TCP) — RS-485 обычно общая шина на несколько устройств, между запросами
стоит выдерживать паузу, а не бомбардировать её как отдельный TCP-сокет.

Проверено на реальном протоколе Modbus RTU (framing + CRC16 через настоящие `FluentModbus.ModbusRtuClient`/
`ModbusRtuServer`), но без физического COM-порта — виртуализирован только транспорт (петлевой TCP-сокет
вместо `System.IO.Ports.SerialPort`, `FluentModbus.IModbusRtuSerialPort` это и позволяет), сама логика
опроса/кодирования — тот же код, что реально работает с настоящим портом
(`tests/PGW.Core.Tests/ModbusRtuTests.cs`). Перед реальным вводом в эксплуатацию всё равно стоит
проверить с конкретным устройством на реальном порту/кабеле — таймауты и электрика RS-485 на практике
не то же самое, что loopback-тест.

## REST API (§14.2)

- `GET/POST/PUT/DELETE /config/channels[/{name}]` — источники (пароли скрыты в ответах)
- `GET/POST/PUT/DELETE /config/channels/{name}/tags[/{tagName}]` — теги внутри источника
- `GET/POST/PUT/DELETE /config/outputs[/{name}]` — выходы
- `GET/POST/PUT/DELETE /config/outputs/{name}/map[/{tag}]` — записи Register Map внутри выхода
- `GET /config/outputs/{name}/map` без Accept — то же самое, но в CSV
- `GET /runtime/tags?prefix=...`, `GET /runtime/tags/{id}` — текущие значения/качество
- `GET /runtime/status` — health/uptime/версия конфига
- `POST /runtime/reload` — применить изменённый конфиг (диф, см. ниже)

Запись (`POST`/`PUT`/`DELETE`) сразу сохраняет файл на диск и валидирует его целиком, но **не**
перезапускает драйверы/интерфейсы сама — иначе десять правок подряд означали бы десять переподключений.
Явно вызови `POST /runtime/reload`, когда закончишь редактировать (панель это делает за тебя, см. ниже).
Reload — это диф, а не «стоп всё/старт всё»: источник или выход, чей конфиг байт-в-байт не изменился,
не трогается вообще — его драйвер/сокет остаётся как был. Правишь один тег в одном источнике — все
остальные источники и **все уже открытые TCP-соединения SCADA-клиентов к другим выходам** переживают
reload без разрыва; перезапускается только то, что реально изменилось (или было добавлено/удалено).
Если поменял именно тот выход, к которому SCADA подключена — это соединение закономерно оборвётся
(порт/настройки сервера пересоздаются), это не баг, это и есть смысл reload.
Тело запроса — тот же набор полей, что в YAML (`{"name": "...", "driver": "modbus_tcp_client", "host": "...", ...}`);
у `PUT` поле из URL (`{name}`/`{tagName}`/`{tag}`) всегда побеждает то же поле в теле.
- `POST /config/opcua/browse` — обход адресного пространства OPC UA сервера для генерации тегов (§5.2):
  `{"source": "plc_main"}` браузит уже настроенный источник его же security/сертификатами, либо
  `{"endpoint": "opc.tcp://host:4840", "use_security": false}` — разовое подключение без записи в
  конфиг. Опционально `"node_id"` (по умолчанию — `ObjectsFolder`) для спуска на уровень ниже.
  Возвращает `[{node_id, browse_name, node_class, has_children}]`; `has_children` — эвристика
  (`Object`/`View` узлы почти всегда её имеют, гарантии нет без реального обхода вглубь).
- `GET /runtime/event-log`, `GET /runtime/api-log`
- `GET /metrics` — Prometheus

`/config/*` и `/runtime/reload` требуют `Authorization: Bearer $PGW_API_TOKEN`, если эта переменная
окружения задана; `/runtime/*` остаётся открытым (см. §14.2). Kestrel слушает `127.0.0.1:8420` по
умолчанию, адрес — `PGW_API_URL`.

## Веб-панель

Тот же процесс, тот же порт, та же база — отдельного сервера или сборки не требуется. Открой
`http://<адрес>:8420/` в браузере. Чистый HTML/CSS/JS без сборки и зависимостей (`src/PGW.Host/wwwroot`),
поверх уже существующего REST API — никакого отдельного бэкенда для UI. Две вкладки:

- **Monitor** — статус шлюза, здоровье источников/выходов, таблица тегов с фильтром и
  live-обновлением (~1.5 с), журнал событий.
- **Config** — добавление/редактирование/удаление источников, тегов внутри них, выходов и записей
  Register Map. У формы источника/тега — явные поля под самое частое (host/port/unit_id для Modbus,
  endpoint для OPC UA, area/address/type) плюс текстовое поле «Доп. поля (JSON)» под всё остальное
  (`deadband`, `write_min`/`write_max`, `security`, ...) — сознательно не городим динамическую форму
  «поля меняются от драйвера» ради компактности кода. Каждое действие сразу пишет на диск; синяя
  плашка сверху появляется после первого изменения и держит кнопку **Reload**, чтобы применить пачку
  правок одним переподключением, а не по одному на каждую. У тега источника с драйвером OPC UA рядом
  с полем NodeId — кнопка **«Обзор…»**, открывающая раскрывающееся дерево реального адресного
  пространства сервера (`POST /config/opcua/browse`), чтобы не гадать/не копипастить NodeId вручную.
- **Modbus Tool** — встроенный аналог ModScan32: читает/пишет любой Modbus TCP слейв по
  host:port/unit id/область/адрес, независимо от `project.yaml` (не нужно заводить источник, чтобы
  просто потыкать регистры руками). Поддерживает все области (Coil/Discrete Input/Input
  Register/Holding Register), все типы данных ядра (`int16`/`uint16`/`int32`/`uint32`/`int64`/
  `uint64`/`float32`/`float64`) и все 4 порядка слов (`ABCD`/`CDAB`/`BADC`/`DCBA`) — значение
  показывается декодированным, плюс HEX и BIN сырых регистров рядом. Клик по ✎ у записи в HR/CO
  открывает запись нового значения. «▶ Опрос» держит соединение открытым и перечитывает по таймеру;
  если опрос временно не удался (устройство моргнуло) — таблица не очищается и не врёт «значение не
  изменилось», а тускнеет, пока связь не восстановится. REST: `POST /tools/modbus/read`,
  `POST /tools/modbus/write`, `POST /tools/modbus/close` (`src/PGW.Drivers.Modbus/ModbusScanner.cs`) —
  за тем же токеном, что и `/config/*`, т.к. пишет в произвольные устройства.

- **Доступ по IP в сети**: по умолчанию Kestrel слушает только `127.0.0.1` (§10, безопасность по
  умолчанию). Чтобы открыть с другого устройства в сети — `PGW_API_URL=http://0.0.0.0:8420` (или
  конкретный IP хоста) при запуске.
- **Как приложение на рабочем столе**: в Chrome/Edge — значок установки в адресной строке или меню
  → «Установить PGW» (это PWA, `manifest.webmanifest` уже подключен). Получится окно без адресной
  строки, с иконкой в панели задач — но данные оттуда идут из того же самого запущенного PGW.Host,
  никакой отдельной desktop-сборки нет и не нужно.

## Диагностика

`_System.*` — обычные RO-теги (`uptime_s`, `heartbeat`, `config_version`,
`_System.<source>.connected/error_count/last_error_code`,
`_System.<output>.client_count/requests_total/exceptions_total`). Они ничем не отличаются от
пользовательских тегов и, как и любой тег, могут быть добавлены в `map:` Modbus-выхода — специального
кода для диагностики в интерфейсе отдачи нет, это тот же общий Register Map.

## Развёртывание

- Linux: `deploy/systemd/pgw.service` (непривилегированный пользователь `pgw`,
  `CAP_NET_BIND_SERVICE` для порта 502)
- Docker: `deploy/Dockerfile` (self-contained `linux-x64`)
- Windows: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`, регистрация службы
  через `.UseWindowsService()` (Session 0, штатно, без NSSM). Готовый zip (exe + wwwroot + demo-конфиг
  + `.bat`-лаунчеры из `deploy/windows/`) собирается автоматически на каждый push через
  `.github/workflows/ci.yml` (job `publish-windows`) и лежит в артефактах соответствующего workflow
  run на вкладке **Actions** репозитория — не нужно просить пересобрать вручную.

## Известные упрощения v0.1 (сознательно, ради компактности кода)

- **OPC UA security policy**: теперь `sources: [...].security.policy` (например `Basic256Sha256`) и
  `.mode` (`Sign`/`SignAndEncrypt`) реально приводят к выбору именно этой политики среди тех, что
  предлагает сервер — `OpcUaEndpointSelector` делает `DiscoveryClient`-запрос за списком реальных
  endpoint'ов сервера и ищет точное совпадение mode+policy, а не просто "лучшую" по мнению
  `CoreClientUtils.SelectEndpoint`. Если сервер не предлагает запрошенную комбинацию — не падаем,
  логируем WARN со списком того, что сервер реально предложил, и откатываемся на прежнее
  best-effort поведение. Аутентификация по сертификату переиспользует собственный сертификат шлюза,
  отдельного flow нет.
- **Browse — по-прежнему один уровень за раз**, не вся страница дерева рекурсивно за один вызов — это
  сознательно оставлено так же, а не "недоделано": рекурсивно тянуть всё дерево заранее на большом
  PLC/DCS означало бы тысячи browse-запросов при открытии формы. Тот же подход у настоящих OPC UA
  клиентов (UaExpert и т.п.) — раскрытие узлов по требованию. Раньше у этого REST-эндпоинта вообще не
  было интерфейса — теперь на вкладке **Config** у поля NodeId тега OPC UA есть кнопка **«Обзор…»**,
  открывающая раскрывающееся дерево адресного пространства прямо в форме редактирования тега.
- **Modbus write whitelist на одном порту**: реализован единый whitelist IP на подключение к выходу
  (§6.1, `whitelist:`); отдельный whitelist "этому IP разрешено читать, но не писать" **на том же
  порту, для разных клиентов** — не реализован. Причина не лень, а честная граница библиотеки:
  `FluentModbus.ModbusServer.RequestValidator` — один делегат
  `(unitId, functionCode, address, quantity) -> ExceptionCode` без какой-либо привязки к тому, с
  какого именно соединения/IP пришёл запрос, так что различить "этому IP только читать" от "этому —
  можно писать" на уровне валидатора нечем. Сделать это правильно означало бы писать собственный
  Modbus-TCP-фреймирующий прокси перед `ModbusTcpServer` (разбирать MBAP-заголовок и код функции
  самим, для каждого read-only клиента гонять его трафик через локальный relay) — заметный кусок
  протокольного кода с нетривиальными краевыми случаями (частичные TCP-чтения, несколько PDU в одном
  сегменте, конкурентные соединения), не тот код, который стоит писать наспех ради галочки.

  **Рабочая альтернатива уже сегодня, без единой новой строки кода** — два отдельных выхода на разные
  порты с уже готовым `whitelist:` на каждом:
  ```yaml
  outputs:
    - name: scada_readers      # только читающие клиенты (историан, HMI-реплика, ...)
      interface: modbus_tcp_server
      port: 502
      read_only: true
      whitelist: ["10.0.0.11", "10.0.0.12"]
      map: [...]               # тот же набор тегов
    - name: scada_writer       # единственный клиент, которому реально нужна запись
      interface: modbus_tcp_server
      port: 5020
      read_only: false
      whitelist: ["10.0.0.20"]
      map: [...]               # та же карта, но rw: true там, где нужно
  ```
  Ту же карту регистров можно продублировать между двумя `outputs:` (Register Map строится независимо
  на каждый выход) — читающие клиенты идут на 502 и физически не могут писать (`read_only: true` на
  уровне всего выхода), пишущий клиент — на отдельный порт под отдельным whitelist. Если позже
  понадобится именно "один порт — разные права по IP" — дай знать, дело за MBAP-прокси выше.
- **on_bad: freeze_and_flag** теперь реально замораживает значение и **выставляет отдельный bit-флаг**
  (Coil/DI), пока источник Bad, и снимает его при восстановлении — задаётся в записи Register Map
  через `flag_area`/`flag_address` (например: `on_bad: freeze_and_flag, flag_area: DI, flag_address: 50`).
  Без `flag_address` — ошибка валидации конфига (использовать `on_bad: hold`, если флаг не нужен).
- `max_connections` теперь реально enforced — `ModbusTcpServer.MaxConnections` (нативное свойство
  библиотеки) ограничивает число одновременных TCP-сессий на выход; подключения сверх лимита
  принимаются на уровне TCP, но не обслуживаются протоколом.
- Валидатор проверяет, что запись Register Map ссылается на реально существующий тег в источнике
  (не только на существующий источник) — полезно именно из-за редактора: удалить тег из источника,
  забыв про ссылающуюся на него запись карты, теперь честно подсвечивается как ошибка при reload/save.
- Redundancy (§14.1), CSV import — вне v0.1 согласно самому ТЗ (§12, этап 7+).

Все взаимодействия проверены end-to-end на реальных сокетах (не моках), в обе стороны:
Modbus↔Modbus (`ModbusRoundTripTests`, `CoreIsolationTests`) и OPC UA (`OpcUaRoundTripTests` — реальный
`SimulatedOpcUaServer` из `PGW.Simulator`, реальная подписка, реальная запись, подтверждённая через
подписку же). Дополнительно прогнан ручной сценарий с обоими протоколами в одном демо (`pgw simulate` +
`demo/project.yaml`): SCADA-клиент читает и пишет через Modbus-выход в тег, который физически хранится
на симулированном OPC UA сервере — то есть кросс-протокольная запись Modbus→OPC UA подтверждена вживую,
не только модульным тестом. Перед реальным продакшеном всё равно стоит проверить против настоящего
OPC UA сервера конкретного вендора — политики безопасности и типы данных на практике отличаются.

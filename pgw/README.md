# PGW — Protocol Gateway

Modbus TCP + OPC UA → единое пространство тегов → Modbus TCP Server. Реализация v0.1 по `TZ_protocol_gateway_v0_1.md`.

```
Modbus TCP Client ──┐                      ┌── Modbus TCP Server
                     ├──▶ Tag Space (core) ─┤
OPC UA Client     ───┘   (value+quality     └── (REST /runtime, /config)
                          +timestamp)
```

Ядро (`PGW.Core`) не содержит ни строчки, специфичной для Modbus или OPC UA — оно работает только через
контракты `IProtocolDriver`/`IProtocolInterface`. Это подтверждено тестом `CoreIsolationTests`
(фейковый драйвер третьего "протокола" проходит через реальный Modbus TCP Server без единой правки в
ядре или интерфейсе).

## Структура

```
src/PGW.Core            — Tag Space, контракты, конфиг, YAML-загрузчик, cross-platform пути
src/PGW.Drivers.Modbus   — Modbus TCP Client (master) + Modbus TCP Server (slave)
src/PGW.Drivers.OpcUa    — OPC UA Client
src/PGW.Host             — composition root: DI-сборка драйверов/интерфейсов, REST API, Worker Service,
                           wwwroot/ — веб-панель (см. "Веб-панель" ниже)
tests/PGW.Core.Tests     — unit- и end-to-end тесты (реальные TCP-сокеты, без моков протокола)
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
```

## Быстрый просмотр без реального оборудования

`demo/` — готовый стенд: симулированный Modbus-PLC + конфиг под него, чтобы увидеть панель с живыми
данными сразу после `git clone`, без настройки настоящих устройств. Два терминала:

```bash
# терминал 1 — симулированный PLC на 127.0.0.1:15020
dotnet run --project demo/SimulatedPlc

# терминал 2 — сам шлюз с демо-конфигом
cd src/PGW.Host
PGW_CONFIG=../../demo/project.yaml dotnet run          # Windows PowerShell: $env:PGW_CONFIG="..\..\demo\project.yaml"; dotnet run
```

Открой `http://localhost:8080/` — значения `T1_supply`/`P1_supply` будут меняться каждые ~0.7 с.

Тесты (включают реальный Modbus round-trip через loopback-сокеты, не моки):

```bash
dotnet test PGW.sln
```

## Конфигурация

Один YAML-файл (`gateway` / `sources` / `outputs`), формат — см. `project.sample.yaml` и §7.2 ТЗ.
Путь по умолчанию: `/etc/pgw/project.yaml` (Linux) или `%ProgramData%\PGW\config\project.yaml`
(Windows); переопределяется переменной `PGW_CONFIG`.

Пароли — только через `password_env: ИМЯ_ПЕРЕМЕННОЙ` в конфиге OPC UA `security:`, не текстом в YAML.

## REST API (§14.2)

- `GET/POST/PUT/DELETE /config/channels[/{name}]` — источники (пароли скрыты в ответах)
- `GET/POST/PUT/DELETE /config/channels/{name}/tags[/{tagName}]` — теги внутри источника
- `GET/POST/PUT/DELETE /config/outputs[/{name}]` — выходы
- `GET/POST/PUT/DELETE /config/outputs/{name}/map[/{tag}]` — записи Register Map внутри выхода
- `GET /config/outputs/{name}/map` без Accept — то же самое, но в CSV
- `GET /runtime/tags?prefix=...`, `GET /runtime/tags/{id}` — текущие значения/качество
- `GET /runtime/status` — health/uptime/версия конфига
- `POST /runtime/reload` — применить изменённый конфиг (см. ограничение ниже)

Запись (`POST`/`PUT`/`DELETE`) сразу сохраняет файл на диск и валидирует его целиком, но **не**
перезапускает драйверы/интерфейсы сама — иначе десять правок подряд означали бы десять переподключений.
Явно вызови `POST /runtime/reload`, когда закончишь редактировать (панель это делает за тебя, см. ниже).
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
окружения задана; `/runtime/*` остаётся открытым (см. §14.2). Kestrel слушает `127.0.0.1:8080` по
умолчанию, адрес — `PGW_API_URL`.

## Веб-панель

Тот же процесс, тот же порт, та же база — отдельного сервера или сборки не требуется. Открой
`http://<адрес>:8080/` в браузере. Чистый HTML/CSS/JS без сборки и зависимостей (`src/PGW.Host/wwwroot`),
поверх уже существующего REST API — никакого отдельного бэкенда для UI. Две вкладки:

- **Monitor** — статус шлюза, здоровье источников/выходов, таблица тегов с фильтром и
  live-обновлением (~1.5 с), журнал событий.
- **Config** — добавление/редактирование/удаление источников, тегов внутри них, выходов и записей
  Register Map. У формы источника/тега — явные поля под самое частое (host/port/unit_id для Modbus,
  endpoint для OPC UA, area/address/type) плюс текстовое поле «Доп. поля (JSON)» под всё остальное
  (`deadband`, `write_min`/`write_max`, `security`, ...) — сознательно не городим динамическую форму
  «поля меняются от драйвера» ради компактности кода. Каждое действие сразу пишет на диск; синяя
  плашка сверху появляется после первого изменения и держит кнопку **Reload**, чтобы применить пачку
  правок одним переподключением, а не по одному на каждую.

- **Доступ по IP в сети**: по умолчанию Kestrel слушает только `127.0.0.1` (§10, безопасность по
  умолчанию). Чтобы открыть с другого устройства в сети — `PGW_API_URL=http://0.0.0.0:8080` (или
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
  через `.UseWindowsService()` (Session 0, штатно, без NSSM)

## Известные упрощения v0.1 (сознательно, ради компактности кода)

- **Reload** переприменяет всю конфигурацию (drivers/interfaces создаются заново), а не делает
  точечный diff — активные соединения Modbus-клиентов с выходом при reload разрываются. Полный
  «hot reload без разрыва» из §7.3 — не реализован.
- **OPC UA**: security policy выбирается через `CoreClientUtils.SelectEndpoint(url, useSecurity)`
  (лучшая подходящая конечная точка сервера), а не точный выбор конкретной политики среди нескольких
  предложенных сервером. Аутентификация по сертификату переиспользует собственный сертификат шлюза,
  отдельного flow нет. Browse — один уровень за раз (без рекурсивного обхода всего дерева за один вызов).
- **Modbus write whitelist**: реализован единый whitelist IP на подключение к выходу (§6.1); отдельный
  whitelist "разрешено читать, но не писать" не реализован — таких клиентов библиотека не различает
  на уровне валидатора запроса.
- **on_bad: freeze_and_flag** ведёт себя как `hold` (значение не подстановка); отдельного bit-флага
  качества на тег в Register Map нет — используйте `_System.<source>.connected` как общий признак.
- `max_connections` хранится в конфиге, но не enforced жёстко (нет отслеживания живых соединений).
- Валидатор проверяет, что запись Register Map ссылается на реально существующий тег в источнике
  (не только на существующий источник) — полезно именно из-за редактора: удалить тег из источника,
  забыв про ссылающуюся на него запись карты, теперь честно подсвечивается как ошибка при reload/save.
- Redundancy (§14.1), CSV import — вне v0.1 согласно самому ТЗ (§12, этап 7+).

Все взаимодействия Modbus↔Modbus проверены end-to-end (реальные сокеты, реальная запись в обе стороны).
OPC UA-драйвер собран и проверен по актуальному API SDK, но не тестировался против живого OPC UA
сервера в этом окружении — перед продакшеном проверьте против реального/симулированного PLC.

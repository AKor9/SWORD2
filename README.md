# SWORD2

Экшен на C#/.NET 10 и DirectX 12 (Vortice.Windows): мечник против врагов, бой на анимациях, попадания, разрезы
и рэгдолл. SWORD2 — та же игра, что SWORD, на новой архитектуре: движок, игра и пекарня контента разведены,
симуляция отделена от клиента, мир — на своём ECS, движок рассчитан на работу ИИ-агентов.

Состояние: **этап 1, точка 1е** — агентский доступ: Claude Code и другие MCP-клиенты подключаются к
`Sword.Headless --agent` и живому `Sword.exe --agent` (`.mcp.json`, [docs/agent-playbook.md](docs/agent-playbook.md)):
время, мир, команды, снимок кадра, устройство тика. До того — инструменты прототипа (HUD, F4, бенч) и клиент (WASD,
RTS-камера SWORD, плавное движение при любой частоте кадров). План, решения и этапы —
[ARCHITECTURE_PLAN.md](ARCHITECTURE_PLAN.md); правила репозитория — [AGENTS.md](AGENTS.md).

## Требования

Windows 10/11 x64, .NET SDK 10.0.400 или новее (`global.json`), GPU с D3D12 Feature Level 12_1, Resource Binding
Tier 3 и Shader Model 6.6. Agility SDK и DXC приезжают NuGet-пакетами.

## Сборка и проверка

```
dotnet build SWORD.slnx
dotnet test SWORD.slnx
powershell -NoProfile -ExecutionPolicy Bypass -File tools/scripts/verify.ps1
```

## Устройство

| папка | что |
|---|---|
| `engine/` | движок: основа, ECS, контент, форматы, платформа, D3D12, рендер, UI, симуляция (Runtime), клиент, агентский доступ |
| `game/` | игра: контракты, симуляция, клиент, модули, композиция (Bootstrap), exe `Sword` и `Sword.Headless` |
| `tools/` | пекарня контента (с этапа «Пекарня») и скрипты проверки |
| `tests/` | тесты, в том числе архитектурные; эталон SWORD — `tests/reference/` |
| `docs/` | архитектура, решения, рецепты для агентов |

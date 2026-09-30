# Источники и границы проверки

Источники VRChat проверялись 29 сентября 2026; настройки MCP и sandbox Codex
повторно сверены 30 сентября 2026. Это первичные источники.
Сведения о проектах и клиентах могут меняться; перед установкой сверяйте версии.
Функции сторонних инструментов приводятся по их документации, а не по нашему
тесту в Unity. Рабочие правила и шаблоны этого комплекта разработаны под запрос
пользователя и не являются официальной политикой VRChat или OpenAI.

| ID | Источник | Что подтверждает |
| --- | --- | --- |
| S1 | [VRChat Agentic Tools](https://github.com/sentfromspacevr/vrchat-agentic-tools) | Unity-мост, execute_csharp, конфигурация клиентов и вспомогательные проверки |
| S2 | [Codex Memories](https://learn.chatgpt.com/docs/customization/memories) | Отдельная локальная память, настройка и отложенное формирование |
| S3 | [Codex MCP](https://learn.chatgpt.com/docs/extend/mcp?surface=cli) | Подключение серверов и per-tool approval settings |
| S4 | [Codex approvals](https://learn.chatgpt.com/docs/agent-approvals-security) | Режим чтения, запросы разрешений и границы sandbox |
| S5 | [Configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference) | Имена и значения параметров конфигурации |
| S6 | [AGENTS.md](https://developers.openai.com/codex/agent-configuration/agents-md) | Загрузка проектных инструкций и ограничения объёма |
| S7 | [Codex best practices](https://learn.chatgpt.com/guides/best-practices) | Короткие постоянные правила и отдельные рабочие материалы |
| S8 | [VRCFury installation](https://vrcfury.com/download/) | Установка через VCC |
| S9 | [Modular Avatar](https://modular-avatar.nadena.dev/docs/intro) | Назначение компонентов, VCC/ALCOM |
| S10 | [VCC getting started](https://vcc.docs.vrchat.com/guides/getting-started/) | Создание Avatar-проекта и подбор редактора |
| S11 | [Allowed avatar components](https://creators.vrchat.com/avatars/whitelisted-avatar-components/whitelisted-avatar-components/) | Допустимые компоненты VRChat-аватаров |
| S12 | [Current Unity version](https://creators.vrchat.com/sdk/upgrade/current-unity-version/) | Поддерживаемая версия редактора |
| S13 | [Performance Ranks](https://creators.vrchat.com/avatars/avatar-performance-ranking-system/) | Показатели производительности и платформенные различия |
| S14 | [Эксперименты автора моста](https://sentfromspace.xyz/blog/claude-vrchat-avatar/) | Примеры автоматизации и наблюдавшиеся ошибки |

В архиве отсутствуют исходники VRChat Agentic Tools, модели, платные ассеты,
ключи API, исполняемый установщик и собственный механизм блокировки на стороне
Unity. Подключение, проверка границ самостоятельной работы и тесты на аватаре выполняются
пользователем в целевом окружении.

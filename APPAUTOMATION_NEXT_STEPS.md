# AppAutomation

Шаблон адаптирован к ContactMirror: настоящий MainWindow, стабильные AutomationId, Headless и Windows FlaUI, тестовый host с вымышленными данными. Сценарии и команды — в [README UI tests](tests/ContactMirror.UiTests.Authoring/README.md); фактические результаты и границы — в [VALIDATION](docs/VALIDATION.md).

На этой машине Tooling запускается через закреплённый DLL fallback из-за устаревшего глобального resolver cache. Сам cache не менялся. Developer shell имеет доступ к Windows input desktop; это не доказывает UI в чистой установке или реальный вход Google.

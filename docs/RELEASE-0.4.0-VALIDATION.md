# ContactMirror 0.4.0: проверка доставки

12.10.2026 пользователь отдельно разрешил слияние и публикацию после приёмки редактора. [PR #2](https://github.com/Kibnet/ContactMirror/pull/2) слит в `main`; [стабильный релиз0.4.0](https://github.com/Kibnet/ContactMirror/releases/tag/v0.4.0) опубликован как Latest, не draft и не prerelease.

## Source и проверки

- Feature commits: `1fa82b8` — полный редактор, `5382dc1` — понятные формы и UX; metadata: `cf04335` — версия/changelog/curated notes.
- Merged main и тег `v0.4.0`: `a5771d15d1d9800cbbed8994914fc35d5502dcbe`; tree `387dbd281c1a55193390c159d7898c56347d10fb`.
- Production package собран **после merge** из точного SHA тега. Packaged assembly имеет ProductVersion `0.4.0+a5771d15d1d9800cbbed8994914fc35d5502dcbe`; её SHA256 совпадает с опубликованным staging.
- Полная принятая реализация:451/451,0skips; см. [UI/field audit](FRIENDLY-CONTACT-FIELDS-VALIDATION.md). После version stamp повторно прошли Release build0errors/warnings, Core163/163 и Google/storage185/185. UI runtime source относительно полного UI-прогона не менялся.
- GitHub Actions/обязательных status checks в этом репозитории нет; CI не заявляется. PR merge был CLEAN/MERGEABLE, проверен exact head SHA.

## Production package

App ID `ContactMirror.Desktop`, профиль Production, канал `win`, RID `win-x64`, GitHub источник `https://github.com/Kibnet/ContactMirror`. Desktop OAuth client совпадает с установленной0.3.0; содержимое credentials не выводилось и не коммитилось.

`Build-Release.ps1` проверил compiled profile, staging и feed. `Publish-Release.ps1` выполнен без `-Publish`: он проверил Setup/portable/nupkg/feed и создал SHA256SUMS. Затем загружены ровно пять разрешённых assets с curated `docs/releases/0.4.0.md`. Перед переводом draft в stable проверены uploaded state, размеры/SHA256 каждого asset, target SHA/tag и точное совпадение release body с curated notes. Стандартные notes скрипта не публиковались.

| Asset | Bytes | SHA256 |
|---|---:|---|
| ContactMirror.Desktop-0.4.0-full.nupkg |55046272|`1aeb7fd46e234cacacc5419dbc1df7e58d44689bad64de23361728b6a3cee183`|
| ContactMirror.Desktop-win-Setup.exe |59676800|`a61dfb4befa1084e7db0b357702eee77c9ec349d7b7b7bdb729b4c6f9b6a1f03`|
| ContactMirror.Desktop-win-Portable.zip |54876900|`826014898b9b8d09521075fff614298ad00b18017b865fc0ae368499403beeca`|
| releases.win.json |276|`a27e8e438af66a081fbc57b521de14cabfaa46b42bc7d93d6340bae5d5785df6`|
| SHA256SUMS.txt |400|`50759a78f54c0e986764df1f0e3e448f1fea2091f77c129005f12a428902e126`|

Само приложение `VelopackUpdateBackend.VerifyPackageFile` проверило full package size/SHA256, единственный nuspec, ID/version/channel/RID/OS/architecture/mainExe и наличие ContactMirror.dll. Packaged assembly SHA256: `50b56b44abbe67196c24268860b70e712dd5e149419845a5ffd2e16c2756cef8`. Проверены bundled OAuth/update configuration и Avalonia.Controls.DataGrid dependency. PDB, test binaries и vault отсутствуют.

## Публичное обновление

Прочитаны metadata существующей установки0.3.0 (`sq.version`) и её update source. `VelopackUpdateBackend` и `UpdateSourceOptions` не изменились между0.3.0 и tag0.4.0.

Probe использовал фактический backend приложения и Velopack1.2.0 TestLocator: App ID/current version/channel прежние, RootAppDir/PackagesDir в отдельном ignored evidence-каталоге. GitHub токен не передавался. `CheckAsync` обнаружил0.4.0; `DownloadAsync` скачал публичный full package55046272 bytes и выполнил штатную проверку пакета. SHA256 скачанного файла совпал с local/feed/uploaded SHA256. Assembly и `sq.version` существующей установки совпали с хэшами до проверки.

**Discovery/download/verification PASS; `applied=false`.** Никакого вызова ApplyAndRestart, установки, перезапуска рабочей программы или обращения к настоящему Google не было. Пользователь может завершить обновление в настройках установленной программы: проверить обновления, скачать, установить и перезапустить. Формат контактов/настроек сохраняется; updater не выполняет downgrade. Возможное исправление доставляется0.4.1, ручной rollback требует отдельной переустановки.

## Evidence и review

Локальные ignored artifacts: `chat-artifacts/release-0.4.0/{build.log,core/core.trx,google/google.trx,provenance.json,package-build.log,package-verification.json,oauth-continuity.json,publication-plan.json,uploaded-verification.json,public-release.json,public-update.json,public-update.log}`; probe source — `public-update-probe/Program.cs`. Build manifest/OAuth source paths не прикладывались к публичному релизу. Generated artifacts не коммитятся.

Separate full delivery review завершён **PASS**: Scope/Evidence → Contract → Adversarial → Fix/re-review → Roles → Stop. Reviewer лично сверил source/tree/tag/package provenance, metadata/curated notes, пять local/public asset hashes, скачанный пакет и сохранность установки. MEDIUM по использованию curated notes и LOW по rollback clarification исправлены и повторно проверены; открытых findings нет.

Domain/UX: принятую реализацию и её границы сохранены; migration/нового поведения при доставке нет. Tester: полные UI/field проверки, version-stamp revalidation, exact-source Production packaging и реальный public discovery/download согласованы. Architect: ID/channel/RID/config/dependencies и исходный backend сохранены. Delivery/security: точный main/tag, стабильный Latest release, curated notes, пять проверенных assets, OAuth continuity и отсутствие installed apply подтверждены.

Reviewer работает в unrestricted sandbox и выполняет только чтение: процедурный advisory/adversarial fallback, без технической read-only изоляции; build/tests/probe/UI/mutations не запускал. Depth/NoFindingJustification: unsupported claims, unrelated changes, authorization, hidden install/config/API contracts, binary provenance и rollback сверены; manual-review challenge закрыт фактическим anonymous download/hash и сохранением existing installation. Известные ограничения релиза: Google OAuth Testing, installer не подписан; installed apply и реальный Google не проверены. Stop: публикация и требуемая доступность обновления завершены; этот audit фиксируется отдельным docs commit без изменения release tag/binaries.

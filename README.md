# ASF-RandomWishlistAdditions

Плагин для **[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)**, который для каждого залогиненного бота через случайные интервалы добавляет в вишлист одну случайную игру из текущих "Новинок" и "Специальных предложений" Steam — имитация того, как живой человек периодически браузит магазин и откладывает что-то на потом.

У каждого бота свой независимый цикл: раз в случайное число минут в диапазоне `[MinDelayMinutes; MaxDelayMinutes]` бот пытается добавить одну игру в вишлист. Кандидаты берутся из публичного стора Steam (`New Releases` + `Specials`, обновляется раз в `CandidatePoolCacheHours` часов, общий пул на все боты), из него исключаются:

- игры, которыми бот уже владеет;
- игры, уже стоящие в вишлисте этого бота;
- AppID, которые при проверке через `appdetails` оказались не игрой (DLC, саундтрек, инструмент и т.п.) — такие запоминаются и больше не предлагаются никому из ботов до перезапуска процесса.

Как и в `RandomBotFriends`, у каждого бота случайная **цель** — не бесконечное добавление, а случайное число в диапазоне `[TargetMinCount; TargetMaxCount]`, назначаемое один раз при первом запуске бота в рамках текущего процесса. После достижения цели бот перестаёт что-либо добавлять.

## Установка

1. Скачайте архив плагина из [Releases](../../releases) и распакуйте в папку `plugins` рядом с ASF (создайте подпапку с именем плагина).
2. Перезапустите ASF.

## Конфигурация

Настройки задаются **глобально**, в `ASF.json`, как дополнительные (нераспознанные ASF) свойства верхнего уровня:

```json
{
	"RandomWishlistAdditionsEnabled": true,
	"RandomWishlistAdditionsMinDelayMinutes": 360,
	"RandomWishlistAdditionsMaxDelayMinutes": 1440,
	"RandomWishlistAdditionsTargetMinCount": 3,
	"RandomWishlistAdditionsTargetMaxCount": 10,
	"RandomWishlistAdditionsCandidatePoolCacheHours": 6
}
```

| Свойство | Тип | По умолчанию | Описание |
| --- | --- | --- | --- |
| `RandomWishlistAdditionsEnabled` | `bool` | `false` | Включает/выключает плагин. |
| `RandomWishlistAdditionsMinDelayMinutes` | `ushort`, минуты | `360` | Нижняя граница случайной паузы между попытками добавить игру в вишлист. |
| `RandomWishlistAdditionsMaxDelayMinutes` | `ushort`, минуты | `1440` | Верхняя граница случайной паузы между попытками. |
| `RandomWishlistAdditionsTargetMinCount` | `byte` (0-255) | `3` | Нижняя граница случайной цели по числу добавлений на бота (за время жизни процесса). |
| `RandomWishlistAdditionsTargetMaxCount` | `byte` (0-255) | `10` | Верхняя граница случайной цели по числу добавлений на бота. |
| `RandomWishlistAdditionsCandidatePoolCacheHours` | `ushort`, часы | `6` | Как долго кэшировать список кандидатов из New Releases/Specials (общий на все боты), прежде чем запросить заново. |

Если в любой из пар `Min` больше `Max`, значения меняются местами автоматически. Цель по добавлениям не персистится между перезапусками ASF — при рестарте каждому боту снова назначается новая случайная цель, и он снова начинает считать добавления с нуля (уже реально добавленные ранее игры при этом никуда не денутся и не будут задублированы, так как перед каждой попыткой плагин сверяется с актуальным вишлистом).

> Про фильтрацию мусора: Steam иногда показывает в этих категориях не только игры, но и DLC/саундтреки/утилиты. Плагин проверяет каждого кандидата через публичный `appdetails` перед добавлением и отбрасывает всё, что не `type: "game"` — но это не железная гарантия на 100% (появление новых, ранее не встречавшихся видов контента возможно).

## Сборка

Проект использует **[ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate)** и собирается вместе с исходниками ASF, подключёнными как git submodule:

```sh
git clone --recurse-submodules https://github.com/buddymurdock/ASF-RandomWishlistAdditions.git
cd ASF-RandomWishlistAdditions
dotnet build -c Release
```

Если репозиторий уже склонирован без `--recurse-submodules`, подтяните submodule отдельно:

```sh
git submodule update --init --recursive
```

## Лицензия

Apache-2.0, см. [LICENSE.txt](LICENSE.txt).

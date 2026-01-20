# DonationAlerts для Streamer.bot

Актуальная интеграция DonationAlerts для Streamer.bot с OAuth 2.0, автообновлением токенов и удобными аргументами для кастомных действий.

**Что уже есть**
- Автоматическое подключение через браузер
- Подписка на донаты и цели через Centrifugo WebSocket
- Больше аргументов для действий (донат, цель, метаданные)
- Чистые логи и мягкий реконнект
- Настройки через глобальные переменные

## Быстрый старт: подключение

1. Создайте приложение в DonationAlerts.

   Откройте:
   https://www.donationalerts.com/application/clients

2. Укажите URL перенаправления (Redirect URL).

   По умолчанию интеграция использует:

   ```
   http://127.0.0.1:8554/donationalerts/callback/
   ```

   Если хотите другой адрес или порт, измените и в приложении, и в настройках ниже.

3. Заполните глобальные переменные в Streamer.bot.

   Минимум:
   - `daClientId`
   - `daClientSecret`

4. Создайте действия в Streamer.bot.

   Рекомендуемая схема:
   - **DA | Connect** -> метод `Connect`
   - **DA | Start** -> метод `Start` (запускать в фоне)
   - **DA | Stop** -> метод `Stop`

5. Запустите **DA | Connect**.

   Откроется браузер. Завершите авторизацию.

6. Запустите **DA | Start**.

   Это включит прослушивание донатов и целей.

## Настройки (глобальные переменные)

| Переменная | Что делает | По умолчанию |
| --- | --- | --- |
| `daClientId` | ID клиента (Client ID) из DonationAlerts | обязательно |
| `daClientSecret` | Секрет клиента (Client Secret) из DonationAlerts | обязательно |
| `daRedirectUrl` | URL перенаправления (Redirect URL) для OAuth | `http://127.0.0.1:8554/donationalerts/callback/` |
| `daScopes` | Области доступа OAuth (scopes) | `oauth-user-show oauth-donation-subscribe oauth-goal-subscribe` |
| `daAuthTimeoutSeconds` | Таймаут ожидания авторизации | `90` |
| `daReconnectDelaySeconds` | Пауза между реконнектами | `5` |
| `daMaxReconnectAttempts` | Лимит реконнектов (0 = бесконечно) | `0` |
| `daDownloadAudio` | Скачивать аудио-сообщения | `true` |
| `daUserAgent` | Заголовок User-Agent для запросов | `StreamerBot-DonationAlerts/2.0` |
| `daUpdateRepo` | Репозиторий для обновлений (`owner/repo`) | пусто |

Токены сохраняются автоматически:
- `daAccessToken`
- `daRefreshToken`
- `daAccessTokenExpiresAt`
- `daSocketToken`
- `daUserId`

## Аргументы для обработчиков донатов

Интеграция передает аргументы в действия, вызываемые при донате.

**Основные**
- `daDonationId`
- `daDonationName`
- `daDonationUsername`
- `daDonationMessageType` (`text` или `audio`)
- `daDonationMessage`
- `daDonationAmount`
- `daDonationCurrency`
- `daDonationIsShown`
- `daDonationCreatedAt`
- `daDonationShownAt`
- `daDonationReason`
- `daDonationSeq`
- `daDonationRaw` (JSON)

**Совместимость со старой версией**
- `daName`
- `daUsername`
- `daType`
- `daMessage`
- `daAmount`
- `daCurrency`
- `daAmountConverted`
- `daAudioUrl`
- `daAudio` (путь к файлу, если `daDownloadAudio = true`)

## Аргументы для обновлений целей

- `daGoalId`
- `daGoalIsActive`
- `daGoalIsDefault`
- `daGoalTitle`
- `daGoalCurrency`
- `daGoalCurrent`
- `daGoalTarget`
- `daGoalReason`
- `daGoalSeq`
- `daGoalRaw` (JSON)

## Имена действий по умолчанию

Скрипт ищет эти действия в Streamer.bot:

- `DonationHandler_Default`
- `DonationHandler_GoalHandler`
- `DonationHandler_After`

Если хотите, добавьте действия с точной суммой доната:

- `DonationHandler_100`
- `DonationHandler_499.99`

## Обновления

Если хотите уведомления об обновлениях:

1. Создайте действие **DA | Update Checker** с `updateChecker.cs`.
2. Укажите репозиторий в `daUpdateRepo`, например:

```
my-org/streamerbot-donationalerts
```

## Если что-то не работает

- Проверьте, что URL перенаправления совпадает в DonationAlerts и в `daRedirectUrl`.
- Убедитесь, что **DA | Start** запущен в фоне.
- Проверьте scopes в `daScopes`.
- Если авторизация не проходит, снова запустите **DA | Connect**.

---

Основано на старой версии play_code и полностью обновлено по актуальной документации DonationAlerts и Streamer.bot.

## Документация

- https://www.donationalerts.com/apidoc
- https://docs.streamer.bot/api
- https://docs.streamer.bot/api/csharp


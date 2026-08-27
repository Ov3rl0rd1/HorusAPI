Мне нужно чтобы ты без измениния кода (только просмотр и анализ), дал мне план интеграции эквайринга в мое API. Нужна архитектура с помошью которой я смогу быстро сменить плетжного провайдера в случае необходимости. Еще расчитывай что планируется система промокодов и плюс тариф подписки "для своих" (отдельная стоимость, тариф появляется только если его выдал я лично, либо поменял поле в бд, или через админ endpoint). В слачае надобности сообщи есть ли смысл хранить информацию о платежах в локальной бд. Так же подскажи чем я могу воспользоваться для создания безопасных облачных бекапов БД (желательно бесплатное или дешевое решение, надеюсь такие найдутся ведь объем моей БД крайне мал (100-1000 пользователей, 5-50 серверов), но надежность, конечно, важнее). Еще нужна возможность как оформления подписки так и единоразовый платеж. Возврат будет делаться только через поддержку так что этот функционал идет как admin endpoint, а вот возможность отменить подписку должна быть и у пользователя. Можешь так же добавить от себя чего бы ты доделал/исправил или реализовал. Так же если будут появляться вопросы, то не гадай, задавай их мне. Нужна архитектура с заделом на будущее изменения (только запускаю сервис и думаю что будет еще много исправлений, доработок и нового функционала). Далее вся информация по работе с API и webhook текущего платежного провайдера (не для интеграции, а для понимая от чего отталкиваться в архитектуре):

# 1. Введение

1. Введение
Platega API позволяет интегрировать платежные функции в ваши сервисы: создавать платежи, проверять статусы, получать отчёты.
Все запросы выполняются в формате JSON по протоколу HTTPS.
Базовый URL:
https://app.platega.io/
2. Авторизация
Для начала работы с нашем API в заголовки запросов (headers) нужно передать 2 параметра.

| Key          |	Value            |
|--------------|---------------------|
| X-MerchantId |	<Ваш MerchantId> |
| X-Secret     |	  <Ваш API ключ> |

# 2. Платежи
## 2.1. Рекурентные платежи (подписки)
### Создание подписки
Создать подписку
POST
/transaction/process
Подписка — это регулярное автосписание с плательщика через СБП. Вы один раз создаёте подписку и отправляете плательщика на нашу платёжную форму; дальше привязку, активацию и все списания делаем мы, а вам приходят callback'и. Баланс пополняется по каждому успешному списанию.
Плательщик на платёжной форме вводит email, подтверждает привязку счёта в своём банке (СБП/НСПК) — подписка становится Active. Дальше мы автоматически списываем amount каждый период. Вам ничего вызывать не нужно.
Денежная транзакция на этом шаге не создаётся — транзакции появляются позже, по каждому списанию.
Request

Header Params
X-MerchantId
string 
required
X-Secret
string 
required

Body Params
application/json
Required
paymentMethod
integer 
required
Всегда 6 (число, не строка)
paymentDetails
object 
required
amount
integer 
required
Сумма одного регулярного списания
currency
string 
required
"RUB"
interval
enum<string> 
(SubscriptionInterval)
SubscriptionInterval
required
Период списаний: 1 — день, 2 — неделя, 3 — месяц, 4 — год
Allowed values:
1
2
3
30 дней
4
intervalCount
integer 
required
Лимит зависит от interval: день до 31, неделя до 4, месяц до 12, год до 3
description
string 
required
Показывается плательщику на платёжной форме и в email-уведомлениях

Responses
🟢200
application/json
Успешно создано
Bodyapplication/json
paymentMethod
string 
required
transactionId
string 
required
transactionId здесь — это ID подписки (subscriptionId). Сохраните его: по нему приходят callback'и и работают все ручки ниже
redirect
string 
required
Плательщика нужно отправить на redirect сразу: на подтверждение привязки даётся 30 минут, после чего подписка переходит в Failed.
status
string 
required
merchantId
string 
required
🟠400
🟠401

пример

curl --location '/transaction/process' \
--header 'X-MerchantId;' \
--header 'X-Secret;' \
--header 'Content-Type: application/json' \
--data '{
    "paymentMethod": 6,
    "paymentDetails": {
        "amount": 500,
        "currency": "RUB",
        "interval": 3,
        "intervalCount": 1
    },
    "description": "Premium подписка"
}'

ответ

{
    "paymentMethod": "Subscription",
    "transactionId": "11111111-1111-1111-1111-111111111111",
    "redirect": "https://pay.platega.io/subscription/11111111-...",
    "status": "PENDING",
    "merchantId": "22222222-2222-2222-2222-222222222222"
}
### Получить подписку
Получить подписку
GET
/subscription/{subscriptionId}
Возвращает подписку по указанному ID

Request

Path Params
subscriptionId
string 
required
Header Params
X-MerchantId
string 
required
X-Secret
string 
required

Responses

🟢200
application/json
Данные транзакции
Bodyapplication/json
id
string 
required
status
string 
required
amount
integer 
required
currencyCode
string 
required
intervalUnit
string 
required
intervalCount
integer 
required
startAt
string 
required
nextChargeAt
string 
required
lastChargeAt
string 
required
description
string 
required
createdAt
string 
required
customerEmail
string 
required
chargeMetrics
object 
required
chargesTotal
integer 
required
chargesSuccess
integer 
required
chargesFailed
integer 
required
totalAmount
integer 
required
lastChargeAt
string 
required
nextChargeAt
string 
required

🟠404

пример

curl --location '/subscription/' \
--header 'X-MerchantId;' \
--header 'X-Secret;'

ответ

{
    "id": "11111111-1111-1111-1111-111111111111",
    "status": "Active",
    "amount": 100,
    "currencyCode": "RUB",
    "intervalUnit": "Month",
    "intervalCount": 1,
    "startAt": "2026-07-08T09:00:00Z",
    "nextChargeAt": "2026-08-09T09:10:00Z",
    "lastChargeAt": "2026-07-09T09:10:00Z",
    "description": "Premium подписка",
    "createdAt": "2026-07-08T09:00:00Z",
    "customerEmail": "payer@example.com",
    "chargeMetrics": {
        "chargesTotal": 1,
        "chargesSuccess": 1,
        "chargesFailed": 0,
        "totalAmount": 100,
        "lastChargeAt": "2026-07-09T09:10:00Z",
        "nextChargeAt": "2026-08-09T09:10:00Z"
    }
}
### Отменить подписку
Отменить подписку
POST
/subscription/{subscriptionId}/cancel
Отмена останавливает будущие списания. Ручка идемпотентна. Плательщик также может отменить подписку сам — по ссылке из email, которое мы отправляем после каждого списания; вы узнаете об этом из SUBSCRIPTION_CANCELLED.

Request

Path Params

subscriptionId
string 
required

Header Params

X-MerchantId
string 
required
X-Secret
string 
required

Responses

🟢200
application/json
Успешно создано
Bodyapplication/json
subscriptionId
string 
required
status
string 
required

🟠400

🟠401

пример

curl --location --request POST '/subscription//cancel' \
--header 'X-MerchantId;' \
--header 'X-Secret;'

ответ

{
    "subscriptionId": "11111111-1111-1111-1111-111111111111",
    "status": "cancelled"
}
### Callback по списанию
Callback по списанию
Webhook
POST
subscriptionTransactionStatus
Приходит на каждое списание — успешное и неуспешное. Отличается от callback'а обычного платежа только двумя дополнительными полями: SubscriptionId и NextChargeAt.

Request

Header Params

X-MerchantId
string 
optional
Ваш MerchantId (UUID)
X-Secret
string 
optional
Ваш API ключ

Body Params

application/json
Required
Id
string 
required
Id — ID транзакции-списания (новый на каждое списание).
Amount
integer 
required
Currency
string 
required
Status
enum<string> 
(PaymentStatus)
PaymentStatus
required
CONFIRMED — деньги списаны, баланс пополнен (сумма за вычетом комиссии).
CANCELED — списание не прошло: баланс не меняется, NextChargeAt = null, подписка переходит в PastDue (провайдер не будет повторять попытки).
Allowed values:
PENDING
CANCELED
CONFIRMED
CHARGEBACKED
PaymentMethod
integer 
required
Payload
string 
required
SubscriptionId
string 
required
NextChargeAt
string 
required

Examples

Responses

🟢200
OK
This response does not have a body.

примеры

curl --location 'https://your-api-server.com' \
--header 'X-MerchantId;' \
--header 'X-Secret;' \
--header 'Content-Type: application/json' \
--data '{
    "Id": "33333333-3333-3333-3333-333333333333",
    "Amount": 100,
    "Currency": "RUB",
    "Status": "CONFIRMED",
    "PaymentMethod": 6,
    "Payload": "",
    "SubscriptionId": "11111111-1111-1111-1111-111111111111",
    "NextChargeAt": "2026-08-09T09:10:00Z"
}'

curl --location 'https://your-api-server.com' \
--header 'X-MerchantId;' \
--header 'X-Secret;' \
--header 'Content-Type: application/json' \
--data '{
    "Id": "33333333-3333-3333-3333-333333333333",
    "Amount": 100,
    "Currency": "RUB",
    "Status": "CANCELED",
    "PaymentMethod": 6,
    "Payload": "",
    "SubscriptionId": "11111111-1111-1111-1111-111111111111",
    "NextChargeAt": null
}'
### Callback по статусу подписки
Callback по статусу подписки
Webhook
POST
subscriptionStatus
Приходит при смене статуса. В нём Id = SubscriptionId (ID подписки, не транзакции).

Request

Header Params

X-MerchantId
string 
optional
Ваш MerchantId (UUID)
X-Secret
string 
optional
Ваш API ключ

Body Params

application/json
Required
Id
string 
required
Amount
integer 
required
Currency
string 
required
Status
enum<string> 
(CallbackSubscriptionStatus)
CallbackSubscriptionStatus
required
Статус подписки в Callback
Allowed values:
SUBSCRIPTION_ACTIVATED
Подписка активна, списания выполняются по расписанию
SUBSCRIPTION_PAST_DUE
Перманентный, из него нет переходов в другой статус, если нет вебхука об успешной оплате или отмене
SUBSCRIPTION_CANCELLED
Переход из ACTIVATED или PAST_DUE — при явной отмене мерчантом или плательщиком (через ссылку отмены или API)
SUBSCRIPTION_FAILED
Переход из ACTIVATED — при невозможности привязки в момент первой активации (провайдер вернул ошибку или не подтвердил согласие)
PaymentMethod
integer 
required
Payload
string 
required
SubscriptionId
string 
required
NextChargeAt
string 
required

Examples

Responses

🟢200
OK
This response does not have a body.

примеры

curl --location 'https://your-api-server.com' \
--header 'X-MerchantId;' \
--header 'X-Secret;' \
--header 'Content-Type: application/json' \
--data '{
    "Id": "11111111-1111-1111-1111-111111111111",
    "Amount": 100,
    "Currency": "RUB",
    "Status": "SUBSCRIPTION_ACTIVATED",
    "PaymentMethod": 6,
    "Payload": "",
    "SubscriptionId": "11111111-1111-1111-1111-111111111111",
    "NextChargeAt": "2026-08-09T09:10:00Z"
}'

curl --location 'https://your-api-server.com' \
--header 'X-MerchantId;' \
--header 'X-Secret;' \
--header 'Content-Type: application/json' \
--data '{
    "id": "00000000-0000-0000-0000-000000000000",
    "amount": 1000,
    "currency": "RUB",
    "status": "CANCELED",
    "paymentMethod": 2
}'
## 2.2. Создание платежной ссылки без заданного метода
Создание платежной ссылки без заданного метода
POST
v2/transaction/process
Создает транзакцию и возвращает данные для оплаты. ID транзакции генерируется системой автоматически — не передавайте поле id в запросе.
При посещении страницы плательщик сам выбирает способ оплаты.
Метаданные платежа
Для магазинов отдельных категорий необходимо передавать поле metadata с идентификатором плательщика. Уточните у вашего менеджера, требуется ли это для вашего магазина.
Отсутствие metadata.userId при наличии требования отключает антифрод-защиту и может привести к отключению магазина.
Редирект через Telegram-бота
По умолчанию при использовании paymentMethod: 13 (криптоплатежи) пользователь перенаправляется на веб-пейформу.
Если вы хотите, чтобы оплата проходила через Telegram-бота — обратитесь к вашему менеджеру для подключения.

Request

Header Params

X-MerchantId
string 
required
X-Secret
string 
required

Body Params

application/json
Required
paymentDetails
object 
required
amount
number 
required
currency
string 
required
description
string 
required
return
string 
required
failedUrl
string 
required
payload
string 
optional
metadata
object 
optional
userId
string 
required
Уникальный идентификатор плательщика в вашей системе (например, Telegram user ID). Используется антифрод-системой.
userName
string 
required
Любые дополнительные данные о плательщике, которые вы хотите сохранить вместе с транзакцией.

Examples

Responses

🟢200
application/json
Успешно создано
Bodyapplication/json
transactionId
string 
required
status
string 
required
url
string 
required
expiresIn
string 
required
rate
number 
required

🟠400

🟠401

пример

curl --location 'v2/transaction/process' \
--header 'X-MerchantId;' \
--header 'X-Secret;' \
--header 'Content-Type: application/json' \
--data-raw '{
    "paymentDetails": {
        "amount": 500,
        "currency": "RUB"
    },
    "description": "Оплата мешков картошки клиенту №293",
    "return": "https://google.com/success",
    "failedUrl": "https://google.com/fail",
    "payload": "Дополнительная информация о платеже",
    "metadata": {
        "userId": "123456789",
        "userName": "@username",
        "clientIp": "111.47.86.11"
    }
}'

ответ

{
    "transactionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "status": "PENDING",
    "url": "https://pay.platega.io/?id=f8000067-a4a0-0000-0000-2556a0b40000&mh=0a0000a4-0000-0000-0000-200004060000",
    "expiresIn": "00:15:00",
    "rate": 91.2
}

# 3. Возвраты
## 3.1. Проверка возможности отмены транзакции
Проверка возможности отмены транзакции
GET
/transaction/{id}/cancel-supported
Возвращает информацию о том, доступна ли отмена для указанной транзакции, и какая сумма в USDT будет списана с баланса при её проведении.
Для успешного ответа (supported: true) необходимо, чтобы на одном из балансов мерчанта (USDT или RUB) было достаточно средств для покрытия суммы возврата.

Request

Path Params
id
string 
required

Header Params

accept
string 
required
Example:
text/plain
X-MerchantId
string 
required
Example:
29ef6fa6-0d2b-466c-9604-0363a30436cc
X-Secret
string 
required
Example:
iStHENoXjHdy78A4tGG3M6TzqLvtRe335bbIGGYYx1SfIzHzQyggz3ZA903OH1TwODhQEZv6HxV9GF6IryaMuMVlFYG03sZsJbpg

Responses

🟢200

Success
application/json
supported
boolean 
required
true — отмена доступна и баланс достаточен. false — отмена невозможна
totalDeductUsdt
number 
required
Итоговая сумма в USDT, которая будет списана с баланса при проведении отмены
penaltyNativeAmount
number 
optional
penaltyNativeCurrency
string 
optional
Валюта штрафа (RUB, EUR и т.д.)
penaltyUsdt
number 
required
penaltyConversionRate
number 
optional
Курс конвертации, применённый при расчёте штрафа
blockReason
string 
optional
Причина блокировки, если supported: false по балансу. Например: "Insufficient funds".

пример

curl --location '/transaction//cancel-supported' \
--header 'accept: text/plain' \
--header 'X-MerchantId: 29ef6fa6-0d2b-466c-9604-0363a30436cc' \
--header 'X-Secret: iStHENoXjHdy78A4tGG3M6TzqLvtRe335bbIGGYYx1SfIzHzQyggz3ZA903OH1TwODhQEZv6HxV9GF6IryaMuMVlFYG03sZsJbpg'

ответ

{
  "supported": true,
  "totalDeductUsdt": 0.01236094,
  "penaltyNativeAmount": null,
  "penaltyNativeCurrency": null,
  "penaltyUsdt": null,
  "penaltyConversionRate": null,
  "blockReason": null
}
## 3.2. Отмена транзакции
Отмена транзакции
POST
/transaction/{id}/cancel
Инициирует отмену транзакции и возврат средств плательщику. Перед вызовом рекомендуется проверить возможность отмены через cancel-supported.

Request

Path Params
id
string 
required

Header Params

accept
string 
required
Example:
text/plain
X-MerchantId
string 
required
Example:
29ef6fa6-0d2b-466c-9604-0363a30436cc
X-Secret
string 
required
Example:
iStHENoXjHdy78A4tGG3M6TzqLvtRe335bbIGGYYx1SfIzHzQyggz3ZA903OH1TwODhQEZv6HxV9GF6IryaMuMVlFYG03sZsJbpg

Responses

🟢200
Success
application/json
transactionId
string 
required
Идентификатор транзакции
accepted
boolean 
required
Принята ли отмена. false означает, что требуется ручная обработка
manualControlRequired
boolean 
required
Если true — отмена не может быть выполнена автоматически, необходимо обратиться в поддержку
message
string 
required
Сообщение о статусе отмены

пример

curl --location --request POST '/transaction//cancel' \
--header 'accept: text/plain' \
--header 'X-MerchantId: 29ef6fa6-0d2b-466c-9604-0363a30436cc' \
--header 'X-Secret: iStHENoXjHdy78A4tGG3M6TzqLvtRe335bbIGGYYx1SfIzHzQyggz3ZA903OH1TwODhQEZv6HxV9GF6IryaMuMVlFYG03sZsJbpg'

ответ

{
  "transactionId": "71f1375c-ba7a-4e9d-84a5-452f3f9cf4c3",
  "accepted": false,
  "manualControlRequired": true,
  "message": "Возврат в процессе"
}

# 4. Callback об изменении статуса транзакции
Callback об изменении статуса транзакции
Webhook
POST
paymentStatus
Ваш endpoint для приема callback. Укажите URL в ЛК (Настройки → Callback URLs). Поставщик отправляет заголовки X-MerchantId и X-Secret и JSON-тело. При успешной оплате — статус CONFIRMED, при неуспешной — CANCELED, при возврате денежных средств — CHARGEBACKED. В случае отсутствия успешного ответа в течение 60 секунд запрос отменяется, затем выполняется до 3 повторных попыток с интервалом 5 минут.
Правила заполнения поля callback:
✅ Использовать только HTTPS (HTTP запрещён)
✅ Использовать только публичные IP-адреса или доменные имена
✅ Требуется корректный SSL-сертификат, выданный доверенным удостоверяющим центром
❌ Самоподписанные (self-signed) SSL-сертификаты не допускаются
❌ Запрещены приватные IP-диапазоны (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.0/8)
❌ Запрещены localhost и loopback адреса

Request

Header Params
X-MerchantId
string 
required
Ваш MerchantId (UUID)
X-Secret
string 
required
Ваш API ключ

Body Params

application/json
Required
id
string <uuid>
required
ID транзакции
amount
number <float>
required
currency
string 
required
status
enum<string> 
required
Статус транзакции в callback
Allowed values:
CONFIRMED
CANCELED
paymentMethod
integer 
optional
ID метода оплаты
payload
string 
optional
Дополнительные данные

Examples

Responses

🟢200
OK
This response does not have a body.

примеры

{
    "id": "00000000-0000-0000-0000-000000000000",
    "amount": 1000,
    "currency": "RUB",
    "status": "CONFIRMED",
    "paymentMethod": 2
}

{
    "id": "00000000-0000-0000-0000-000000000000",
    "amount": 1000,
    "currency": "RUB",
    "status": "CANCELED",
    "paymentMethod": 2
}
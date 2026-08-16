# IdemPay

Платёжный сервис с асинхронной обработкой и поддержкой idempotency.

## Запуск

```bash
# С помощью docker-compose (рекомендуется)
docker-compose up --build

# Или
docker compose up --build
```

Проверено на Docker 28.x и docker-compose v2.

## Сценарий работы

### 1. Создать операцию

```bash
curl -X POST http://localhost:8080/operations \
  -H "Content-Type: application/json" \
  -d '{
    "operationId": "op-12345",
    "amount": "99.99",
    "currency": "RUB",
    "description": "Test payment"
  }'
```

Ответ:
```json
{
  "operationId": "op-12345",
  "status": "CREATED",
  "providerPaymentId": null
}
```

### 2. Отправить на обработку

```bash
curl -X POST http://localhost:8080/operations/op-12345/submit
```

Операция переходит в `PROCESSING`. Фоновый сервис `RecoveryService` будет пробовать отправить платёж провайдеру.

### 3. Проверить статус

```bash
curl http://localhost:8080/operations/op-12345
```

Пока провайдер не вернёт callback, статус будет `PROCESSING`. После успешного callback — `COMPLETED`.

### 4. Посмотреть историю событий

```bash
curl http://localhost:8080/operations/op-12345/events
```

Пример:
```json
[
  {
    "type": "CREATED",
    "fromStatus": null,
    "toStatus": "CREATED",
    "message": "Operation created",
    "occurredAt": "2026-08-16T18:00:00Z"
  },
  {
    "type": "SUBMITTED",
    "fromStatus": "CREATED",
    "toStatus": "PROCESSING",
    "message": "Payment submitted to provider",
    "occurredAt": "2026-08-16T18:00:05Z"
  },
  {
    "type": "COMPLETED",
    "fromStatus": "PROCESSING",
    "toStatus": "COMPLETED",
    "message": "Payment completed by provider",
    "occurredAt": "2026-08-16T18:00:15Z"
  }
]
```

## Хостинг базы данных

Данные хранятся в SQLite в `/data/app.db` (объёмный том `candidate-data`), поэтому база сохраняется между перезапусками.


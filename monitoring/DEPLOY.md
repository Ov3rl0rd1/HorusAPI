# Развёртывание мониторинга — пошагово

Порядок здесь не косметический: каждый шаг опирается на предыдущий, и половина
проблем при установке берётся из попытки начать с середины.

Проверка после каждого этапа встроена в него же. Если она не проходит — дальше идти
незачем, следующий шаг всё равно не заработает.

---

## 0. Сначала слить ветки — без этого ничего не поедет

Деплой-ветки отстают от `dev`, а серверы тянут именно их. Пока это не сделано, любой
`git pull` на сервере принесёт старый код.

```bash
# API: деплой идёт по пушу в main (.github/workflows/deploy.yaml)
cd C:\HorusAPI
git checkout main && git merge --ff-only dev && git push origin main

# Нода: horus-update тянет ветку release (UPDATE_CHANNEL по умолчанию), НЕ dev
cd C:\Users\nrbud\source\repos\Horus-ServerInstance
git checkout release && git merge --ff-only dev && git push origin release
```

Форк ядра (`Xray-core-RTC`) уже в `main` — образ `ghcr.io/ov3rl0rd1/xray-core-rtc:latest`
собирается GitHub Actions. **Дождитесь зелёной сборки** прежде чем обновлять ноды: без
неё метрики комнат olcRTC не появятся, а `horus-update` подтянет прежний образ и вы
решите, что что-то сломано.

**Проверка:** сборки в Actions зелёные во всех трёх репозиториях.

---

## 1. Что понадобится

| | |
|---|---|
| VPS под мониторинг | 1 vCPU / 1 ГБ / 10 ГБ, **отдельный от всего остального** |
| Telegram | бот от @BotFather + chat_id |
| (по желанию) VPS в России | для пробника блокировок, самый дешёвый |

**Почему отдельный сервер.** Не ради ресурсов — всё это занимает ~350 МБ. Сторож,
живущий на охраняемом объекте, не может сообщить, что объект сгорел. Алерт «главный
сервер не отвечает» обязан приходить откуда-то ещё.

Если всё же ставить на главный — работать будет, но о его падении вы не узнаете.

---

## 2. Один токен на весь парк

```bash
openssl rand -hex 24
```

Запишите. Он понадобится **на каждом** сервере и на монитор-сервере — значение должно
совпадать везде.

Это **не** `NODE_API_PASSWORD`. Токен только читает телеметрию; скомпрометированный
монитор-сервер не должен уметь заводить или удалять пользователей.

---

## 3. Монитор-сервер

```bash
git clone <HorusAPI> /opt/horus && cd /opt/horus/monitoring
cp .env.example .env

# tr -d '\n' обязателен: завершающий перевод строки в файле пароля читается
# по-разному разными реализациями, а провалившийся скрейп выглядит как мёртвый сервер
openssl rand -hex 24 | tr -d '\n' > vm/secrets/metrics_password   # ← тот самый токен из шага 2
chmod 600 vm/secrets/metrics_password

nano .env        # TELEGRAM_BOT_TOKEN, TELEGRAM_CHAT_ID
docker compose up -d
```

**Проверка:**

```bash
docker compose ps                 # пять сервисов, am-init в Exited(0) — так и задумано
curl -s localhost:8428/health     # должно ответить
```

---

## 4. Главный сервер

```bash
cd /opt/horus       # или где он у вас
git pull

echo 'METRICS_TOKEN=<токен из шага 2>' >> .env

# --build обязателен: в образ nginx добавлен apache2-utils, без него htpasswd
# не соберётся и метрики отдадут 404. Перезапуск контейнера тут не поможет —
# он заново запустит старый образ, что выглядит ровно как кеш браузера.
docker compose up -d --build nginx
docker compose up -d
```

**Проверка:**

```bash
curl -su horus:<токен> https://<домен>/metrics/host | head -3
curl -su horus:<токен> https://<домен>/metrics/containers | head -3
curl -s https://<домен>/metrics/host | head -1     # без пароля — должно быть 401
```

---

## 5. Каждая нода

```bash
ssh root@<нода>
echo 'METRICS_TOKEN=<тот же токен>' >> /opt/horus/.env
horus-update --now
```

`horus-update` сам пересоберёт локальный nginx — он видит, что каталог `nginx/` изменился.
Ручной `--build` не нужен.

**Что произойдёт заодно, и это нормально:** сертификаты переезжают из `/etc/xray/certs`
в `/etc/horus/certs` (иначе свежая нода вообще не поднимается), поэтому конфиг
перерендерится и **xray один раз перезапустится**. Живые сессии в этот момент оборвутся
и переподключатся. Делайте не в час пик.

**Проверка:**

```bash
curl -su horus:<токен> https://<нода>:8444/metrics/agent | grep horus_xray_up
curl -su horus:<токен> https://<нода>:8444/metrics/host  | head -3
```

Ожидаемо `horus_xray_up 1`. Если `/metrics/agent` отдаёт 404 — токен не доехал до
контейнера nginx (`docker compose up -d nginx` подхватит изменившийся .env).

---

## 6. Прописать серверы

На монитор-сервере, в `monitoring/targets/`:

- `hosts.yml` — главный и все ноды (`/metrics/host`)
- `agents.yml` — **только ноды** (`/metrics/agent`)
- `containers.yml` — главный; ноду добавлять только если включили на ней cadvisor

Правки подхватываются сами в течение минуты, перезапускать нечего.

**Имя в `targets` обязано быть тем, которое покрывает сертификат сервера.** Сертификат на
другое имя роняет скрейп с ошибкой x509, и это выглядит как «сервер лёг» — ровно та
ловушка, которая когда-то съела вечер на Hysteria.

---

## 7. Проверка целиком

```bash
# Все цели должны быть up
curl -s localhost:8428/api/v1/targets \
  | jq '.data.activeTargets[] | {job:.labels.job, server:.labels.server, health, lastError}'

# Правила загрузились: infra, horus, blocking
curl -s localhost:8880/api/v1/rules | jq '.data.groups[].name'
```

**Графики.** vmui слушает только `127.0.0.1` — у VictoriaMetrics **нет аутентификации**:

```bash
ssh -L 8428:127.0.0.1:8428 root@<монитор>
```

и `http://localhost:8428/vmui` → вкладка **Dashboards** → «Horus — серверы» и
«Horus — сервис».

**Телеграм.** Проверьте живьём, а не на глаз: остановите `node-exporter` на одной ноде и
дождитесь `ServerUnreachable` (3 минуты). Потом верните.

---

## 8. Пробник блокировок в России — по желанию

Косвенный сигнал (`NodeReachableButNobodyConnects`) работает сразу и ничего не требует.
Прямой требует площадки в стране.

Сначала на **монитор-сервере** включить аутентификацию, иначе открытый наружу порт — это
и запись метрик, и vmui, и API удаления рядов:

```yaml
# monitoring/docker-compose.yml, victoria-metrics
command:
  - -httpAuth.username=horus
  - -httpAuth.password=<пароль>
```

плюс `VM_BIND=0.0.0.0` в `.env`, и те же `-datasource.basicAuth.*` /
`-remoteWrite.basicAuth.*` для vmalert — он ходит в ту же VictoriaMetrics.

Затем:

```bash
scp -r monitoring/ru-probe root@<ru-vps>:/opt/horus-probe
ssh root@<ru-vps> 'cd /opt/horus-probe && cp .env.example .env && nano .env && docker compose up -d'
```

**Метка `server` в `ru-probe/targets/nodes.yml` обязана совпадать** с `targets/hosts.yml`:
алерт сопоставляет пробу с узлом по ней и при расхождении не сработает никогда — молча.

---

## 9. Что обычно идёт не так

| Симптом | Причина |
|---|---|
| `/metrics/*` отдаёт 404 | `METRICS_TOKEN` пуст. Это защита, а не баг: сервер, которому ничего не настроили, не должен публиковать телеметрию сам по себе |
| **500 с верным паролем, 401 с неверным** | Образ nginx старее коммита `35e6f61`. Файл паролей писался `640 root:root`, а читает его рабочий процесс от пользователя `nginx`. Лечится пересборкой: `docker compose up -d --build nginx`. Симптом обманчив — выглядит как неверный пароль |
| 401 с правильным паролем | В `vm/secrets/metrics_password` завёлся перевод строки. `tr -d '\n'` |
| Цель `up == 0`, в `lastError` x509 | Имя в `targets` не покрыто сертификатом |
| `/metrics/containers` на ноде 502 | cadvisor там выключен по умолчанию. Так и задумано: 50–80 МБ на 700 МБ и одном ядре не лишние. Включается `COMPOSE_PROFILES=containers` в .env ноды |
| vmui пустой на вкладке Dashboards | Формат кастомных дашбордов зависит от версии VM. Скажите — поправлю схему по факту |
| Телеграм молчит | `docker compose logs alertmanager`. Чаще всего chat_id: для группы он отрицательный |

---

## 10. Откат

Мониторинг ничего не меняет в работе сервиса — он только читает. Полный откат:

```bash
# Монитор-сервер
cd /opt/horus/monitoring && docker compose down -v

# На каждом сервере: убрать токен и пересоздать nginx
sed -i '/^METRICS_TOKEN=/d' .env && docker compose up -d nginx
```

После этого `/metrics/*` снова отдают 404, и всё остаётся как было.

Единственное, что откатом не снимается, — переезд сертификатов из шага 5. Он и не должен:
без него не поднимается свежая нода.

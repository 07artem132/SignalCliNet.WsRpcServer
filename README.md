# 📲 SignalCliNet.WsRpcServer

![License](https://img.shields.io/badge/license-GPLv3-blue.svg)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![WebSocket](https://img.shields.io/badge/protocol-WebSocket-brightgreen)
![JSON-RPC](https://img.shields.io/badge/API-JSON--RPC-orange)

**WebSocket JSON-RPC-сервер для взаємодії з месенджером [Signal](https://signal.org/) через бібліотеку [SignalCli.NET](https://github.com/07artem132/SignalCli.NET).**

## 📖 Зміст

1. [🚀 Про проєкт](#-про-проєкт)
2. [✨ Особливості](#-особливості)
3. [📋 Вимоги](#-вимоги)
4. [💻 Встановлення](#-встановлення)
5. [⚙️ Конфігурація](#-конфігурація)
6. [🏃‍♂️ Приклад швидкого старту](#-приклад-швидкого-старту)
7. [📡 Використання та JSON-RPC методи](#-використання-та-json-rpc-методи)
8. [❓ Часті запитання (FAQ)](#-часті-запитання-faq)
9. [🗺️ Дорожня карта](#-дорожня-карта)
10. [🙏 Подяки](#-подяки--проєкт-використовує-такі-відкриті-бібліотеки)
11. [📜 Ліцензія](#-ліцензія)

---

## 🚀 Про проєкт

**SignalCliNet.WsRpcServer** — це самостійний консольний застосунок, який можна запускати й як службу. Він надає WebSocket JSON-RPC-інтерфейс для **простого підключення** до [Signal](https://signal.org/) за допомогою [SignalCli.NET](https://github.com/07artem132/SignalCli.NET).

Він спрощує інтеграцію з Signal, даючи змогу:
- 💬 Надсилати текстові повідомлення, стікери та вкладення.
- 👤 Керувати акаунтами (список акаунтів, синхронізація тощо).
- 📱 Зв'язувати пристрій (лінкування через QR-код).
- 📨 Отримувати й обробляти події (повідомлення, вкладення, реакції тощо) через єдиний двонаправлений WebSocket-канал.

Це усуває необхідність реалізовувати безліч HTTP- та SSE-запитів, адже весь обмін відбувається через **одне** WebSocket-з'єднання з використанням JSON-RPC 2.0.

---

## ✨ Особливості

1. **🔄 Єдиний комунікаційний канал**
   Використання WebSocket спрощує розробку клієнтів: замість окремих механізмів для HTTP POST (запити) та SSE (події) все відбувається в одному каналі.

2. **⚡ Зменшений мережевий overhead**  
   Одне стале з'єднання зменшує витрати на заголовки та повторні рукостискання, що пришвидшує обмін.

3. **🛠️ Автоматичне керування signal-cli**
    - 🚀 Запуск процесу `signal-cli` та моніторинг його стану.
    - 🔄 Перезапуск у разі збоїв (з вказаним лімітом спроб).
    - 🩺 Health-check.

4. **🌐 Кросплатформність**  
   Працює на Windows, Linux та macOS (за умови, що `.NET 10.0+` та `JDK 25+` встановлено).

5. **🔌 Легка інтеграція**  
   Підтримка будь-якої мови програмування, що вміє встановлювати WebSocket-з'єднання й обмінюватися JSON-RPC.

---

## 📋 Вимоги

1. **.NET 10.0** або новіша версія  
   [Завантажити](https://dotnet.microsoft.com/download/dotnet/10.0)

2. **JDK 25+** (signal-cli 0.14.3 потребує class-file 69 = Java 25)  
   [Завантажити](https://www.oracle.com/java/technologies/javase-downloads.html)

3. **signal-cli v0.11.3+**  (вже входить як залежність)

4. **SignalCli.NET** (вже входить як залежність)

---

## 💻 Встановлення

### 1. Завантаження та збірка з вихідного коду

1. Клонуйте репозиторій:
   ```bash
   git clone https://github.com/07artem132/SignalCliNet.WsRpcServer.git
   ```
2. Перейдіть у директорію проєкту:
   ```bash
   cd SignalCliNet.WsRpcServer
   ```
3. Зберіть проєкт:
   ```bash
   dotnet build --configuration Release
   ```
4. Опубліковані збірки будуть доступні в папці:
   ```
   ./bin/Release/net10.0/publish
   ```

### 2. Запуск сервера

```bash
# Windows
SignalCliNet.WsRpcServer.exe

# Linux / macOS (потрібно встановити права на виконання файлу)
chmod +x SignalCliNet.WsRpcServer
./SignalCliNet.WsRpcServer
```

**📝 Примітка**: якщо виникають помилки:
- Переконайтеся, що використовується .NET 10.0 чи новіший.
- Перевірте наявність JDK 25 чи новішої.

---

## ⚙️ Конфігурація

Налаштування виконується через файл `appsettings.json`

| Параметр                                | Опис                                                                                       | Типове значення                                        |
|-----------------------------------------|--------------------------------------------------------------------------------------------|--------------------------------------------------------|
| `Server:Host`                           | Хост (адреса), на якому працюватиме WebSocket-сервер                                      | `localhost`                                           |
| `Server:Port`                           | Порт WebSocket-сервера                                                                    | `9000`                                                |
| `SignalCli:AppHome`                     | Базова директорія для розташування `signal-cli`                                           | `AppDomain.CurrentDomain.BaseDirectory`               |
| `SignalCli:LibDirectory`                | Відносний шлях до директорії з бібліотеками `signal-cli`                                  | `signal-cli/lib`                                      |
| `SignalCli:StoragePathCli`              | Шлях для зберігання даних `signal-cli` (ключі, конфіги тощо)                              | `SignalCliStorageData`                                |
| `SignalCli:MaxRestartAttempts`          | Ліміт спроб перезапустити `signal-cli` у разі збою                                        | `3`                                                   |
| `SignalCli:HealthCheckIntervalSeconds`  | Періодичність (секунди) health-check                                                      | `40`                                                  |
| `SignalCli:HealthCheckTimeoutSeconds`   | Час очікування відповіді (секунди) від `signal-cli` під час health-check                  | `10`                                                  |
| `Logging:LogLevel:Default`             | Рівень логування за замовчуванням                                                         | `Information`                                         |
| `Logging:UseJsonFormat`                | Формат логів JSON (true/false)                                                            | `false`                                               |

---

## 🏃‍♂️ Приклад швидкого старту

1. Відредагуйте `appsettings.json` (за потреби змініть порт, шляхи).
2. Запустіть:
   ```bash
   dotnet run --configuration Release
   ```
3. Переконайтеся, що `signal-cli` працює без помилок (у логах буде інформація про запуск Java-процесу).
4. Підключіться до `ws://localhost:9000` (якщо не змінювали порт).

---

## 📡 Використання та JSON-RPC методи
### 🔌 Приклад підключення (JavaScript)

```javascript
const WebSocket = require('ws');
const ws = new WebSocket('ws://localhost:9000');

ws.on('open', () => {
  // Формуємо JSON-RPC запит
  const request = {
    jsonrpc: '2.0',
    id: 1,
    method: 'ListAccountsAsync',
    params: {}
  };
  
  ws.send(JSON.stringify(request));
});

ws.on('message', (data) => {
  const response = JSON.parse(data);
  console.log('Відповідь:', response);
});
```

### 🐍 Приклад підключення (Python)
```python
import asyncio
import websockets
import json

async def main():
    async with websockets.connect("ws://localhost:9000") as ws:
        request = {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "ListAccountsAsync",
            "params": {}
        }
        await ws.send(json.dumps(request))

        response_data = await ws.recv()
        response = json.loads(response_data)
        print("Відповідь:", response)

asyncio.run(main())
```

### 📚 Основні методи JSON-RPC

Нижче наведено повний список (з прикладами). Усі методи викликаються через JSON-RPC.

#### 👤 1. Керування акаунтами

- **ListAccountsAsync** `{}`

  Отримання списку зареєстрованих акаунтів Signal.

  **Запит**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 1,
    "method": "ListAccountsAsync",
    "params": {}
  }
  ```
  **Відповідь**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 1,
    "result": [
      { "Number": "+380501234567" },
      { "Number": "+380501234568" }
    ]
  }
  ```

- **SendSyncRequestAsync** `{}`

  Надсилає запит на синхронізацію акаунта (груп, контактів тощо).

  **Запит**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 2,
    "method": "SendSyncRequestAsync",
    "params": {}
  }
  ```
  **Відповідь**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 2,
    "result": {}
  }
  ```

#### 👥 2. Керування групами

- **ListGroupsAsync** `{ account: string }`

  Отримання списку груп для вказаного акаунта.

  **Запит**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 3,
    "method": "ListGroupsAsync",
    "params": {
      "account": "+380501234567"
    }
  }
  ```
  **Відповідь**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 3,
    "result": [
      {
        "id": "group-id-base64", 
        "name": "Назва групи",
        "description": "Опис групи",
        "isMember": true,
        "isBlocked": false,
        "messageExpirationTime": 0,
        "members": [
          {
            "number": "+380501234567",
            "uuid": "user-uuid-1"
          },
          {
            "number": "+380501234568",
            "uuid": "user-uuid-2"
          }
        ],
        "pendingMembers": [],
        "requestingMembers": [],
        "admins": [
          {
            "number": "+380501234567",
            "uuid": "user-uuid-1"
          }
        ],
        "banned": [],
        "permissionAddMember": "ONLY_ADMINS",
        "permissionEditDetails": "ONLY_ADMINS",
        "permissionSendMessage": "EVERY_MEMBER",
        "groupInviteLink": "https://signal.group/#group-invite-link"
      }
    ]
  }
  ```

#### 📱 3. Керування пристроями

- **StartLinkAsync** `{}`

  Початок процесу прив'язування нового пристрою (дає URI для QR-коду).

  **Запит**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 4,
    "method": "StartLinkAsync",
    "params": {}
  }
  ```
  **Відповідь**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 4,
    "result": {
      "DeviceLinkUri": "sgnl://linkdevice?uuid=abcdef&pub_key=BASE64KEY"
    }
  }
  ```

- **FinishLinkAsync** `{ deviceLinkUri: string, deviceName: string }`

  Завершення прив'язки пристрою, використовуючи URI та назву.

  **Запит**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 5,
    "method": "FinishLinkAsync",
    "params": {
      "deviceLinkUri": "sgnl://linkdevice?uuid=abcdef&pub_key=BASE64KEY",
      "deviceName": "Мій комп'ютер"
    }
  }
  ```
  **Відповідь**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 5,
    "result": {
      "number": "+380501234567"
    }
  }
  ```

#### 💬 4. Надсилання повідомлень

- **SendTextMessageAsync**

  Надсилання текстового повідомлення одному чи кільком отримувачам.

  **Запит**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 6,
    "method": "SendTextMessageAsync",
    "params": {
      "account": "+380501234567",
      "recipients": ["+380501234568", "+380501234569"],
      "message": "Привіт! Як справи?",
      "externalTextStyles": null,
      "mentions": null,
      "quoteTimestamp": null,
      "quoteAuthor": null,
      "quoteMessage": null,
      "quoteMentions": null,
      "quoteTextStyles": null,
      "quoteAttachments": null,
      "previewUrl": null,
      "previewTitle": null,
      "previewDescription": null,
      "previewImage": null
    }
  }
  ```
  **Відповідь**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 6,
    "result": [
      {
        "results": [
          {
            "recipientAddress": {
              "uuid": "user-uuid-1",
              "number": "+380501234568"
            },
            "type": "SUCCESS"
          },
         {
            "recipientAddress": {
              "uuid": "user-uuid-2",
              "number": "+380501234569"
            },
            "type": "SUCCESS"
          }
        ],
        "timestamp": 1647245682000
      }
    ]
  }
  ```

- **SendAttachmentMessageAsync**

  Надсилання повідомлення з вкладеннями одному чи кільком отримувачам.
> ℹ️ **Існують такі обмеження:** За один раз надіслати прикріплення максимум можливо лише в 1 группу або 10 контактам.

  **Запит**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 7,
    "method": "SendAttachmentMessageAsync",
    "params": {
      "account": "+380501234567",
      "recipients": ["+380501234568"],
      "message": "Фото з останньої зустрічі:",
      "base64Attachments": {
        "image.jpg": "BASE64_ENCODED_CONTENT",
        "document.pdf": "BASE64_ENCODED_CONTENT"
      },
      "externalTextStyles": null,
      "mentions": null,
      "quoteTimestamp": 1647245682000,
      "quoteAuthor": "+380501234569",
      "quoteMessage": "Коли будуть фотографії?",
      "quoteMentions": null,
      "quoteTextStyles": null,
      "quoteAttachments": null
    }
  }
  ```
  **Відповідь**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 7,
    "result": [
      {
        "results": [
          {
            "recipientAddress": {
              "uuid": "group-uuid-1",
              "number": null
            },
            "type": "SUCCESS"
          }
        ],
        "timestamp": 1647245683000
      }
    ]
  }
  ```

- **SendStickerMessageAsync**

  Надсилання стікера одному чи кільком отримувачам.
> **ℹ️ Формат ідентифікатора стикера** має вигляд `pack_id:sticker_id`, де `pack_id` — це ідентифікатор набору стікерів, а `sticker_id` — номер стікера.

  **Запит**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 8,
    "method": "SendStickerMessageAsync",
    "params": {
      "account": "+380501234567",
      "recipients": ["+380501234568"],
      "sticker": "b2e11667c59bce03b6bd13de0377a0b5:32"
    }
  }
  ```
  **Відповідь**:
  ```json
  {
    "jsonrpc": "2.0",
    "id": 8,
    "result": [
      {
        "results": [
          {
            "recipientAddress": {
              "uuid": "user-uuid-1",
              "number": "+380501234568"
            },
            "type": "SUCCESS"
          }
        ],
        "timestamp": 1647245684000
      }
    ]
  }
  ```


#### ⚠️ 5. Обробка помилок

Усі методи JSON-RPC можуть повертати помилки, дотримуючись формату JSON-RPC 2.0:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32000,
    "message": "Помилка виконання методу",
    "data": {
      "stackTrace": "...",
      "message": "Детальний опис"
    }
  }
}
```

---

## ❓ Часті запитання (FAQ)

1. **🔄 Як змінити порт сервера?**  
   Змініть `Server:Port` у `appsettings.json` і перезапустіть.

2. **📎 Чи підтримуються великі вкладення?**  
   Signal загалом підтримує вкладення розміром до ~100 МБ. (Будьте уважні до часу завантаження).

3. **💻 На яких платформах працює сервер?**  
   Перевірено на Windows, Linux і macOS. Потрібні `.NET 10.0+` та сумісна JDK.

---

## 🗺️ Дорожня карта

1. **📡 Реалізація підтримки подій**
    - Підписка на отримання текстових повідомлень, вкладень тощо в реальному часі.
    - Обробка реакцій, статусів (доставлено/прочитано).

2. **🧪 Юніт-тести**
    - Перевірка JSON-RPC-інтерфейсу.
    - Мок-тестування взаємодії з SignalCli.NET.

---

## 🙏 Подяки
Проєкт використовує такі відкриті бібліотеки

| Бібліотека       | Опис                                                                     |
|------------------|--------------------------------------------------------------------------|
| SignalCli.NET    | Базова .NET-бібліотека для взаємодії з `signal-cli`.                      |
| WatsonWebsocket  | WebSocket-сервер для .NET.                                               |
| StreamJsonRpc    | Імплементація протоколу JSON-RPC для .NET.                               |
| Microsoft.Extensions.* | Набір служб .NET для конфігурації, логування, DI-контейнера і хостингу.   |

- [SignalCli.NET (GitHub)](https://github.com/07artem132/SignalCli.NET)
- 
- [JSON-RPC 2.0 Specification](https://www.jsonrpc.org/specification)

---

## 📜 Ліцензія

Проєкт поширюється за ліцензією **GNU General Public License v3.0 (GPLv3)** через використання [signal-cli (GitHub)](https://github.com/AsamK/signal-cli) і [SignalCli.NET](https://github.com/07artem132/SignalCli.NET).

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](http://www.gnu.org/licenses/gpl-3.0.html)

---

> **Розроблено з ❤️ для .NET-спільноти та ЗСУ**. 
> Якщо виникли запитання чи є ідеї — створюйте Pull Request або Issue!
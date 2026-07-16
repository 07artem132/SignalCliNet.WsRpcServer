# Tasks: add-authz-chokepoint

## 1. Chokepoint
- [x] 1.1 Декларативний реєстр політик (незалежний від дискавері); незареєстрований метод → `-32601` до виконання
- [x] 1.2 `SignalRpcSession`: `AuthorizingJsonRpc` замість голого `new JsonRpc`; `Principal` на сесії (V9)
- [x] 1.3 Principal per-invocation з call-context; жодного per-user стану в адаптерах (V8)
- [x] 1.4 Упорядкувати teardown сесії перед auth-стан-машиною: без `async void`/fire-and-forget на критичному шляху (A4)

## 2. IDOR-guards
- [x] 2.1 `AssertAccountAllowed(principal, targetAccount)` на кожному account-bearing методі (W8)
- [x] 2.2 Outbound `FilterReadOutputAsync` — fail-closed (виняток → empty, ніколи raw; W22) + fault-injection тест
- [x] 2.3 R3.2: `listGroups` → лише claim'нуті/власні групи; R3.5: own-linked приватні; R3.6: admin — role-conditional гілка
- [x] 2.4 Валідація+нормалізація recipient (E.164/group-ID), розмір/кодування тексту до dispatch (D11)

## 3. Контракт
- [x] 3.1 Санітизація всіх клієнтських помилок (generic; деталі лише в лог)
- [x] 3.2 `apiVersion` у handshake; mismatch → типізована upgrade-required (A5)
- [x] 3.3 Build-guard: збірка/CI падає на RPC-методі без явної політики (W23)

## 4. Тести (з DoD Фази 2)
- [ ] 4.1 Default-deny: незареєстрований метод → `-32601` без виконання
- [ ] 4.2 Inbound IDOR: A називає account B → відмова
- [ ] 4.3 Outbound IDOR: `listAccounts`/`listGroups` A → лише A (+ admin-гілка R3.6)
- [ ] 4.4 V8: два конекти різних identity не бачать стан одне одного через адаптер
- [ ] 4.5 D11: malformed recipient (не-E.164 / битий group-ID) → відмова на chokepoint, у signal-cli не потрапляє (S6)

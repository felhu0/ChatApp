\# ChatApp



En realtidschatt byggd med ASP.NET Core och SignalR i kursen Datakommunikation och säkerhet på Nackademin (Programutvecklare .NET).



Appen har en öppen lobby för alla inloggade och privata grupprum som är end-to-end-krypterade direkt i webbläsaren. Servern förmedlar och sparar grupprumsmeddelanden, men kan aldrig läsa dem.



\## Funktioner

\- Realtidschatt med SignalR (WebSockets med automatisk fallback)

\- Inloggning med användarnamn krävs för att komma åt chatten

\- All trafik över HTTPS (Hypertext Transfer Protocol Secure) och WSS (WebSocket Secure) med TLS (Transport Layer Security)

\- Registrering och inloggning med lösenord hashade med PBKDF2 (Password-Based Key Derivation Function 2) och JWT (JSON Web Token) vid lyckad inloggning

\- Privata grupprum med inbjudningar. Bara admin får bjuda in, och servern kontrollerar behörigheten mot databasen.

\- End-to-end-kryptering av grupprum: ECDH (Elliptic Curve Diffie-Hellman) för nyckelutbyte och AES-GCM (Advanced Encryption Standard – Galois/Counter Mode) för meddelanden

\- Chattloggar som sparas i databasen och laddas när ett rum öppnas (de 50 senaste per rum)

\- Strukturerad loggning med Serilog till terminal och fil

\- Rate limiting för inloggning, registrering och alla hubbanrop

\- Enhetstester med xUnit och Moq



\## Teknik



\- .NET 10, ASP.NET Core, SignalR

\- EF (Entity Framework) Core + SQLite

\- Serilog

\- xUnit, Moq och SQLite i minnet för tester

\- Web Crypto API (Application Programming Interface) och IndexedDB i webbläsaren

\- HTML, CSS och JavaScript utan ramverk



\## Kom igång lokalt



\### Förutsättningar

\- .NET 10 SDK (Software Development Kit)

\- Verktyget `dotnet-ef`:

```bash

dotnet tool install --global dotnet-ef

```



\### 1. Klona repot

```bash

git clone https://github.com/felhu0/ChatApp.git

cd ChatApp/ChatApp

```



\### 2. Lita på utvecklingscertifikatet

```bash

dotnet dev-certs https --trust

```

Appen använder .NET:s självsignerade utvecklingscertifikat. Kommandot gör att webbläsaren litar på det.



\### 3. Skapa JWT (JSON Web Token)-nyckeln

`Jwt:Key` är serverns hemliga nyckel som signerar alla tokens. Den finns inte i repot, så den som kör projektet skapar en egen. Appen startar inte utan den.



Kör stegen i projektmappen `ChatApp/` (där `ChatApp.csproj` ligger).



Generera en slumpad nyckel på 64 bytes.



PowerShell 7:

```powershell

\[Convert]::ToBase64String(\[System.Security.Cryptography.RandomNumberGenerator]::GetBytes(64))

```



Windows PowerShell 5.1:

```powershell

$b = New-Object byte\[] 64; \[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); \[Convert]::ToBase64String($b)

```



Spara nyckeln i User Secrets:

```bash

dotnet user-secrets init

dotnet user-secrets set "Jwt:Key" "<nyckeln från förra kommandot>"

```

User Secrets lagras utanför projektmappen och hamnar därför aldrig i Git.



\### 4. Skapa databasen

Kör i projektmappen `ChatApp/`:

```bash

dotnet ef database update

```

Databasen (`chatapp.db`) skapas från migrationerna, och lobbyn läggs in automatiskt.



\### 5. Starta appen

Kör i projektmappen `ChatApp/`:

```bash

dotnet run --launch-profile https

```

Öppna https://localhost:7000



\### 6. Testa chatten

1\. Registrera minst två användare via \*\*Skapa konto\*\*. Använd två olika webbläsare, t.ex. Chrome och Edge. Ett privat fönster fungerar också, men då skapas en ny enhetsnyckel varje gång fönstret stängs.

2\. Logga in med båda. \*\*En användare måste ha loggat in minst en gång innan den kan bjudas in\*\*, eftersom den publika nyckeln laddas upp vid inloggning.

3\. Chatta i lobbyn.

4\. Skapa ett grupprum med \*\*+\*\* och bjud in den andra användaren med \*\*Bjud in\*\*.

5\. Valfritt: öppna `chatapp.db` (t.ex. i DB Browser for SQLite). I tabellen `Messages` har lobbymeddelanden text i `Content`, medan grupprumsmeddelanden bara har oläslig `Ciphertext` och `Iv`.



\## Köra testerna



Kör i solution-mappen, en nivå upp från projektmappen:

```bash

cd ..

dotnet test

```



\- `ChatHubTests`: meddelanden, felhantering och behörighet i hubben. Kontrollerar bland annat att avsändaren sätts av servern, att för långa meddelanden nekas, att klartext inte kan skickas till grupprum och att man inte kan gå med i ett grupprum utan medlemskap. Använder Moq för SignalR och en riktig SQLite-databas i minnet.

\- `RoomServiceTests`: anslutning till och frånkoppling från rum

\- `LogSanitizerTests`: skydd mot log injection (kontrolltecken tas bort och långa värden kortas av)



\## Säkerhetsarkitektur



\*\*Ansvarsfördelning\*\*

\- `crypto.js`: all kryptografi. `chat.js`: flödet. Servern: behörighet, validering och lagring – den kan aldrig läsa nycklar eller grupprumsmeddelanden.



\*\*Transport\*\*

\- All trafik över HTTPS (Hypertext Transfer Protocol Secure) och WSS (WebSocket Secure). HSTS (HTTP Strict Transport Security) aktiveras i produktion.



\*\*Lösenord\*\*


\- Hashas med PBKDF2 (Password-Based Key Derivation Function 2) och slumpad salt. Lösenordet sparas aldrig.

\- Samma felmeddelande och svarstid vid fel namn eller lösenord, så att det inte avslöjas vilka användare som finns.



\*\*JWT (JSON Web Token)\*\*


\- Signeras med HS256 och `Jwt:Key` från User Secrets. Servern accepterar bara HS256.

\- Gäller i 30 minuter. Hubbanslutningen stängs när den går ut.

\- Sparas bara i minnet i webbläsaren. Skickas i URL:en till hubben (WebSocket kan inte skicka headers), men query-strängen loggas aldrig.



\*\*Identitet och behörighet\*\*


\- Användarens id och namn läses alltid från token, aldrig från klienten.

\- Rum, historik och inbjudningar kontrolleras mot databasen. Bara admin kan bjuda in.

\- Servern nekar klartext till grupprum, även om klienten är ändrad.



\*\*End-to-end-kryptering av grupprum\*\*


\- Enhetsnyckel: ECDH (Elliptic Curve Diffie-Hellman)-nyckelpar som webbläsaren skapar en gång per användare. Den privata nyckeln är icke-exporterbar och stannar i IndexedDB.

\- Rumsnyckel: AES (Advanced Encryption Standard)-256-nyckel som skaparen av rummet slumpar fram. Krypterar meddelandena.

\- Inslagen rumsnyckel: rumsnyckeln krypterad åt en medlem enligt ECIES (Elliptic Curve Integrated Encryption Scheme). Lagras på servern, som inte kan låsa upp den.

\- Meddelanden krypteras med AES-GCM (Advanced Encryption Standard – Galois/Counter Mode), ny IV (Initialization Vector) per meddelande och rums-id som AAD (Additional Authenticated Data). Servern ser bara ciphertext.

\- Publika nycklar valideras som punkter på kurvan P-256, som skydd mot invalid curve attacks.



\*\*Begränsning av missbruk\*\*


\- Inloggning och registrering: max 5 försök per minut och IP (Internet Protocol)-adress.

\- Hubbanrop: 20 i en snabb följd, sedan cirka 1 per sekund. Max 16 kB per meddelande.

\- Servern validerar längd och format på all indata.



\*\*XSS (Cross-Site Scripting) och loggning\*\*


\- All text visas med `textContent`, aldrig `innerHTML`.

\- Serilog loggar säkerhetshändelser men aldrig lösenord, tokens, nycklar eller meddelanden. Ovaliderad indata rensas med `LogSanitizer` mot log injection.



\## Vad TLS (Transport Layer Security) skyddar och inte skyddar



\*\*Skyddar:\*\*


\- Avlyssning på vägen: ingen på nätverket kan läsa trafiken

\- Manipulation på vägen: ändrad trafik upptäcks

\- Serverns identitet: certifikatet visar att klienten pratar med rätt server (självsignerat bara om det är betrott)

\- Omspelning inom en anslutning



\*\*Skyddar inte:\*\*


\- Data på servern och i databasen: servern ser allt som inte är end-to-end-krypterat, t.ex. lobbyn

\- En komprometterad server eller klient, t.ex. skadeprogram eller XSS (Cross-Site Scripting)

\- Att trafiken stoppas (tillgänglighet)

\- En inloggad användare som missbrukar sin behörighet – hanteras av behörighetskontrollerna



Därför krypteras grupprummen även i applikationslagret, så att innehållet skyddas mot servern och databasen.



\## Kända begränsningar



\- MITM (Man-in-the-Middle) via servern: servern förmedlar publika nycklar och skulle kunna byta ut dem. Förbättring: fingeravtryck som användarna jämför.

\- En enhetsnyckel per webbläsare: en ny webbläsare får en ny enhetsnyckel och kan inte läsa rum som delats med den gamla.

\- Ingen forward secrecy: den som får tag i en enhet kan läsa rummets historik. Medvetet val, så att chattloggarna går att läsa.

\- Replay: en elak server kan skicka ett gammalt krypterat meddelande igen, och mottagaren ser det som nytt.

\- Avsändaren är inte signerad: en elak server skulle kunna byta avsändare. Förbättring: avsändaren i AAD (Additional Authenticated Data).

\- Inslagna rumsnycklar kan bara formatkontrolleras: en elak admin kan skicka en trasig nyckel till en ny medlem.

\- Komprometterad klient: XSS (Cross-Site Scripting) kan använda nycklarna medan sidan är öppen, och servern levererar JavaScript-koden.

\- SignalR laddas från en CDN (Content Delivery Network) utan SRI (Subresource Integrity). Förbättring: `integrity`-attribut.

\- Rate limiting i hubben gäller per anslutning\, inte per användare.

\- Metadata syns för servern: vem, vilket rum, när och ungefärlig längd.

\- Lobbyn är inte end-to-end-krypterad: öppen för alla inloggade, skyddas av TLS (Transport Layer Security).



\## AI-användning



Jag har använt AI (Claude) som stöd under hela projektet, främst för att:

\- få förklaringar av begrepp som ECDH (Elliptic Curve Diffie-Hellman), IV (Initialization Vector), ECIES (Elliptic Curve Integrated Encryption Scheme), JWT (JSON Web Token) och vad TLS (Transport Layer Security) skyddar

\- diskutera designval, t.ex. varför jag valde lagrade inslagna rumsnycklar istället för nya nycklar per session, och varför lobbyn inte är end-to-end-krypterad

\- få förslag på kod, kodgranskning och tydligare kommentarer

\- rita diagram över nyckelflödet för att förstå det



Jag har skrivit och ändrat mycket av koden själv, ställt följdfrågor tills jag förstått varje del och verifierat lösningarna med enhetstester och manuella tester i webbläsaren. Ett exempel är att jag själv testade vad som händer med enhetsnyckeln när jag loggade in i en annan webbläsare.


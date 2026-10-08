// Nyckelhanteringen ligger i klienten eftersom servern aldrig ska kunna läsa nycklar eller meddelanden.
// Servern sköter behörighet, validering och lagring.

// ===== Element på sidan =====
const loginView = document.getElementById("login-view");
const chatView = document.getElementById("chat-view");
const messagesList = document.getElementById("messages");
const messageForm = document.getElementById("message-form");
const messageInput = document.getElementById("message");
const loginError = document.getElementById("login-error");
const roomTitle = document.getElementById("room-title");
const roomSubtitle = document.getElementById("room-subtitle");
const inviteButton = document.getElementById("invite-button");

// Hämtas från servern efter inloggning. Listan styr bara vad som visas,
// servern kontrollerar alltid behörigheten i JoinChatRoom.
let rooms = [];

const lobby = "lobby";
let activeRoom = lobby;
let currentUser = null;
let accessToken = null;         // JWT (JSON Web Token), bara i minnet – inte i localStorage
let deviceKey = null;           // enhetens ECDH (Elliptic Curve Diffie-Hellman)-nyckelpar: { privateKey, publicKeyRaw }
const roomKeys = new Map();     // rumsnyckel- nyckel som skaparen av ett grupprum slumpar fram, delas av alla medlemmar och krypterar meddelandena

// Kö: krypterade meddelanden visas i rätt ordning, även om dekrypteringen tar olika lång tid
let receiveQueue = Promise.resolve();
const roomMessages = new Map(); // roomId -> meddelanden, så att historiken finns kvar vid rumsbyte


// Anslutning till hubben
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/chat", { accessTokenFactory: () => accessToken })  // skickar med JWT (JSON Web Token) vid anslutning
    .withAutomaticReconnect()                                     // försöker återansluta vid avbrott
    .configureLogging(signalR.LogLevel.Warning)                    // bara varningar och fel i konsolen
    .build();


// ===== Hjälpfunktioner =====

// Plockar ut serverns felmeddelande från en HubException
function getErrorMessage(err, fallback) {
    const text = err?.message ?? "";
    const marker = "HubException: ";
    const index = text.indexOf(marker);
    return index >= 0 ? text.substring(index + marker.length) : fallback;
}

// Ikon och namn för ett rum
function roomLabel(room) {
    const icons = { public: "#", direct: "@", group: "👥" };
    return `${icons[room.type]} ${room.name}`;
}

// Kort beskrivning under rubriken
function roomDescription(room) {
    if (room.type === "public") return "Öppet för alla inloggade";
    if (room.type === "direct") return "Direktchatt – bara ni två";
    return room.isAdmin ? "Grupprum – du är admin" : "Grupprum – medlem";
}

// Skapar ett <li> för ett meddelande. All text sätts med textContent skydd mot XSS (Cross-Site Scripting).
function createMessageElement(entry) {
    const li = document.createElement("li");
    li.classList.add("msg");

    if (entry.type === "user") {
        if (entry.user === currentUser) li.classList.add("own");  // egna meddelanden får egen stil

        const name = document.createElement("span");
        name.classList.add("name");
        name.textContent = entry.user;

        const text = document.createElement("span");
        text.textContent = entry.message;

        li.append(name, text);
    } else {
        li.classList.add(entry.type); // "system" eller "error"
        li.textContent = entry.message;
    }
    return li;
}

function scrollToBottom() {
    messagesList.scrollTop = messagesList.scrollHeight;
}

// Sparar ett meddelande i rätt rum och visar det om rummet är aktivt
function addMessage(roomId, entry) {
    if (!roomMessages.has(roomId)) roomMessages.set(roomId, []);
    roomMessages.get(roomId).push(entry);

    if (roomId === activeRoom) {
        messagesList.appendChild(createMessageElement(entry));
        scrollToBottom();
    }
}

// Visar ett fel i det aktiva rummet (sparas inte i historiken)
function showError(message) {
    messagesList.appendChild(createMessageElement({ type: "error", message }));
    scrollToBottom();
}

// ===== Rumslistan =====

// Ritar upp rummen i sina tre sektioner
function renderRoomList() {
    const lists = {
        public: document.getElementById("public-rooms"),
        group: document.getElementById("group-rooms")
    };
    Object.values(lists).forEach(list => list.replaceChildren());

    for (const room of rooms) {
        const button = document.createElement("button");
        button.type = "button";
        button.classList.add("room");
        if (room.id === activeRoom) button.classList.add("active");
        button.textContent = roomLabel(room);
        button.addEventListener("click", () => showRoom(room.id));

        const li = document.createElement("li");
        li.appendChild(button);
        lists[room.type].appendChild(li);
    }
}

// Visar ett rum: rubrik, beskrivning, knappar och historik
function showRoom(roomId) {
    const room = rooms.find(r => r.id === roomId);
    if (!room) return;

    activeRoom = roomId;
    roomTitle.textContent = roomLabel(room);
    roomSubtitle.textContent = roomDescription(room);

    // "Bjud in" syns bara för admin i grupprum (servern kontrollerar ändå)
    inviteButton.classList.toggle("hidden", !(room.type === "group" && room.isAdmin));

    const history = roomMessages.get(roomId) ?? [];
    messagesList.replaceChildren(...history.map(createMessageElement));

    scrollToBottom();       // visa senaste meddelandet
    renderRoomList();       // markera rätt rum i menyn
    messageInput.focus();   // markören direkt i skrivfältet
}

// Dekrypterar ett meddelande med rumsnyckeln 
// Används både för live-meddelanden och historik.
async function addEncryptedMessage(roomId, user, encrypted) {
    const roomKey = roomKeys.get(roomId);
    try {
        if (!roomKey) throw new Error("Ingen rumsnyckel");
        const message = await E2E.decryptMessage(roomKey.key, encrypted, roomId);
        addMessage(roomId, { type: "user", user, message });
    } catch {
        // Misslyckas om nyckeln saknas, meddelandet är ändrat eller flyttat från ett annat rum (AAD, Additional Authenticated Data)
        addMessage(roomId, { type: "error", message: `Ett meddelande från ${user} kunde inte dekrypteras.` });
    }
}

// Hämtar rummets chattlogg från servern. Krypterade meddelanden dekrypteras här.
async function loadHistory(room) {
    const history = await connection.invoke("GetHistory", room.id);
    for (const m of history) {
        if (m.encrypted) {
            await addEncryptedMessage(room.id, m.user, m.encrypted);
        } else {
            addMessage(room.id, { type: "user", user: m.user, message: m.content });
        }
    }
}

// ===== Inloggning och utloggning =====

// Låser upp den inslagna rumsnyckeln (rumsnyckeln krypterad åt mig, förvaras på servern)
// med min privata nyckel. 
async function loadRoomKey(room) {
    if (!room.key) return //lobbyn saknar nyckel
    try {
        roomKeys.set(room.id, await E2E.unwrapRoomKey(room.key, deviceKey.privateKey));
    } catch (err) {
        // T.ex. ny webbläsare: den inslagna rumsnyckeln är inslagen åt en annan enhetsnyckel
        console.warn(`Kunde inte packa upp nyckeln för rummet ${room.name}`, err);
    }
}

// För varje rum: lås upp rumsnyckeln, ladda historiken och gå med i rummet
async function enterRooms() {
    rooms = await connection.invoke("GetMyRooms");// varje grupprum kommer med min inslagna rumsnyckel
    for (const room of rooms) {
        await loadRoomKey(room);
        await loadHistory(room); // historiken först, så att nya meddelanden hamnar efter den
        await connection.invoke("JoinChatRoom", room.id);
    }
}

// Rensar allt i minnet och visar inloggningen
function showLogin() {
    currentUser = null;
    accessToken = null; // token glöms vid utloggning
    deviceKey = null;   // enhetsnyckeln släpps ur minnet, men finns kvar i IndexedDB till nästa inloggning
    roomKeys.clear();    // upplåsta rumsnycklar glöms – de inslagna rumsnycklarna finns kvar på servern
    activeRoom = lobby;
    roomMessages.clear();
    rooms = [];
    messagesList.replaceChildren();
    chatView.classList.add("hidden");
    loginView.classList.remove("hidden");
}

async function logout() {
    showLogin();
    await connection.stop(); // servern tar bort anslutningen från alla rum
}

// ===== Meddelanden från servern =====

// Klartext (lobbyn)
connection.on("ReceiveMessage", (roomId, user, message) =>
    addMessage(roomId, { type: "user", user, message }));

// Systemmeddelanden, t.ex. "Anna has joined"
connection.on("ReceiveSystemMessage", (roomId, message) =>
    addMessage(roomId, { type: "system", message }));

// Krypterat meddelande (grupprum): läggs i kön så att ordningen behålls
connection.on("ReceiveEncryptedMessage", (roomId, user, encrypted) => {
    receiveQueue = receiveQueue.then(async () => addEncryptedMessage(roomId, user, encrypted));
});

// Nytt rum (skapat eller inbjuden). Servern skickar med min inslagna rumsnyckel (rumsnyckeln krypterad åt mig).
connection.on("RoomAdded", async (room) => {
    if (rooms.some(r => r.id === room.id)) return; //redan i listan
    rooms.push(room);    
    try {
        await loadRoomKey(room);
        await loadHistory(room);  // den inbjudna ser även äldre meddelanden
        await connection.invoke("JoinChatRoom", room.id);
    } catch (err) {
        console.error(err);
        showError(getErrorMessage(err, "Kunde inte gå med i rummet."));
    }
    renderRoomList();
});

// Efter avbrott: ny anslutning = nytt ConnectionId, så rummen måste gås med i igen
connection.onreconnected(async () => {
    if (!currentUser) return;
    try {
        roomMessages.clear();
        await enterRooms();
        showRoom(activeRoom)
        addMessage(lobby, { type: "system", message: "Återansluten." });
    } catch (err) {
        console.error(err);
        const message = getErrorMessage(err, "Återanslutningen misslyckades.");
        await logout();
        loginError.textContent = message;
    }
});

// Återanslutningen gav upp (t.ex. token har gått ut): tillbaka till inloggningen
connection.onclose(() => {
    if (!currentUser) return;
    showLogin();
    loginError.textContent = "Anslutningen bröts eller sessionen gick ut. Logga in igen.";
});

// ===== Formulär =====

// Logga in via API:t, få en JWT och anslut sedan till hubben med den
document.getElementById("login-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const passwordInput = document.getElementById("password");
    const username = document.getElementById("username").value.trim();
    const password = passwordInput.value;
    if (!username || !password) return;
    loginError.textContent = "";

    try {
        const response = await fetch("/api/auth/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password })
        });
        passwordInput.value = ""; // lösenordet ska inte ligga kvar i fältet

        if (!response.ok) {
            // Serverns felmeddelande visas med textContent (skydd mot XSS, Cross-Site Scripting)
            loginError.textContent = (await response.text()) || "Inloggningen misslyckades.";
            return;
        }

        const data = await response.json();
        accessToken = data.token; // sparas i minnet
         
        await connection.start();  // ansluter med token (via accessTokenFactory)

        // Hämtar enhetsnyckeln (nyckelpar som webbläsaren skapar en gång per användare) från IndexedDB,
        // eller skapar en första gången. Den publika nyckeln laddas upp till servern.
        deviceKey = await E2E.getOrCreateDeviceKey(data.userName);
        await connection.invoke("SetPublicKey", E2E.toBase64(deviceKey.publicKeyRaw));

        await enterRooms();

        currentUser = data.userName; // namnet som servern har verifierat
        document.getElementById("current-user").textContent = currentUser;
        loginView.classList.add("hidden");
        chatView.classList.remove("hidden");
        showRoom(lobby);
    } catch (err) {
        console.error(err);
        accessToken = null;
        await connection.stop();
        loginError.textContent = getErrorMessage(err, "Kunde inte ansluta till servern.");
    }
});

// Skicka meddelande.
// Lobbyn: klartext (skyddas av TLS, Transport Layer Security).
// Grupprum: krypteras här med rumsnyckeln (nyckel som skaparen av ett grupprum slumpar fram).
// Servern får bara ciphertext och IV (Initialization Vector).
messageForm.addEventListener("submit", async (e) => {
    e.preventDefault();
    const message = messageInput.value.trim();
    if (!message) return;

    const room = rooms.find(r => r.id === activeRoom);

    try {
        if (room?.type === "public") {
            await connection.invoke("SendMessage", activeRoom, message);
        } else {
            const roomKey = roomKeys.get(activeRoom);
            if (!roomKey) {
                showError("Det här rummet saknar nyckel på den här enheten.")
                return;
            }
            const encrypted = await E2E.encryptMessage(roomKey.key, message, activeRoom);
            await connection.invoke("SendEncryptedMessage", activeRoom, encrypted);
        }      
        messageInput.value = "";
    } catch (err) {
        console.error(err);
        showError(getErrorMessage(err, "Meddelandet kunde inte skickas."));
    }
});

document.getElementById("logout").addEventListener("click", logout);

// Skapa ett grupprum. Rumsnyckeln (nyckel som skaparen av ett grupprum slumpar fram) skapas här i webbläsaren.
// Servern får bara den inslagna rumsnyckeln (rumsnyckeln krypterad åt mig).
document.getElementById("new-group").addEventListener("click", async () => {
    const name = prompt("Namn på grupprummet:")?.trim();
    if (!name) return;
    try {
        const roomKey = await E2E.generateRoomKey();
        // Slås in åt mig själv med min publika nyckel,
        // så att jag kan låsa upp rumsnyckeln vid nästa inloggning
        const key = await E2E.wrapRoomKey(roomKey.raw, E2E.toBase64(deviceKey.publicKeyRaw));
        await connection.invoke("CreateGroupRoom", name, key);
        // Rummet kommer tillbaka via "RoomAdded", där den inslagna rumsnyckeln låses upp
    } catch (err) {
        console.error(err);
        showError(getErrorMessage(err, "Rummet kunde inte skapas."));
    }
});

// Bjud in till det aktiva rummet.
// Rumsnyckeln slås in med den inbjudnas publika nyckel
// och blir den inbjudnas inslagna rumsnyckel (rumsnyckeln krypterad åt den medlemmen).
inviteButton.addEventListener("click", async () => {
    const userName = prompt("Vem vill du bjuda in? (användarnamn)")?.trim();
    if (!userName) return;

    const roomKey = roomKeys.get(activeRoom);
    if (!roomKey) {
        // Utan upplåst rumsnyckel finns inget att slå in åt den inbjudna
        showError("Det här rummet saknar nyckel på den här enheten.")
        return;
    }

    try {
        const publicKey = await connection.invoke("GetPublicKey", userName);
        if (!publicKey) {
            // Ingen publik nyckel = användaren har aldrig loggat in och saknar enhetsnyckel
            showError(`${userName} finns inte eller har inte loggat in än.`);
            return;
        }
        const key = await E2E.wrapRoomKey(roomKey.raw, publicKey);
        await connection.invoke("InviteUser", activeRoom, userName, key);
    } catch (err) {
        console.error(err);
        showError(getErrorMessage(err, "Inbjudan misslyckades."));
    }
});

// Visar en bekräftelse om användaren kommer direkt från registreringen
if (new URLSearchParams(window.location.search).has("registered")) {
    document.getElementById("login-info").textContent = "Kontot är skapat. Logga in.";
}
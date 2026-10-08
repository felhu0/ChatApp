// ===== Kryptografi i applikationslagret =====

// All kryptografi sker i webbläsaren med Web Crypto API (Application Programming Interface).
// Servern ser bara publika nycklar, inslagna rumsnycklar och ciphertext – aldrig klartext eller hemliga nycklar.

// Algoritmer (hybridkryptografi)
// - ECDH (Elliptic Curve Diffie-Hellman), P-256: för att dela rumsnyckeln säkert
// - HKDF (HMAC-based Key Derivation Function) med SHA-256 (Secure Hash Algorithm 256): gör om den delade hemligheten till en nyckel
// - AES-GCM (Advanced Encryption Standard – Galois/Counter Mode), 256 bitar: krypterar rumsnycklar och meddelanden
// - Inslagningen följer ECIES (Elliptic Curve Integrated Encryption Scheme)

const E2E = (() => {
    const CURVE = { name: "ECDH", namedCurve: "P-256" }; // ECDH (Elliptic Curve Diffie-Hellman) på kurvan P-256
    const WRAP_INFO = new TextEncoder().encode("ChatApp room key wrap v1"); // kontext för HKDF (HMAC-based Key Derivation Function)
    const DB_NAME = "chatapp-keys";
    const STORE = "deviceKeys";

    // ----- Base64 -----
    // Gör om bytes till text, eftersom SignalR och databasen skickar och lagrar strängar
    function toBase64(bytes) {
        let s = "";
        for (const b of new Uint8Array(bytes)) s += String.fromCharCode(b);
        return btoa(s);
    }

    function fromBase64(b64) {
        return Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    }

    // ----- IndexedDB (webbläsarens lokala databas) -----

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, 1);
            req.onupgradeneeded = () => req.result.createObjectStore(STORE);
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    function dbGet(db, key) {
        return new Promise((resolve, reject) => {
            const req = db.transaction(STORE, "readonly").objectStore(STORE).get(key);
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    function dbPut(db, key, value) {
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, "readwrite");
            tx.objectStore(STORE).put(value, key);
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    }

    // ----- Enhetsnyckel -----

    // Hämtar enhetsnyckeln (nyckelpar som webbläsaren skapar en gång per användare) från IndexedDB,
    // eller skapar en första gången. Den privata nyckeln (hemliga delen av enhetsnyckeln) är icke-exporterbar:
    // den kan användas, men aldrig läsas ut, inte ens av JavaScript.
    async function getOrCreateDeviceKey(userName) {
        const id = userName.toUpperCase();// samma normalisering som servern
        const db = await openDb();
        try {
            const existing = await dbGet(db, id);
            if (existing) return existing;

            const pair = await crypto.subtle.generateKey(CURVE, false, ["deriveBits"]);
            const deviceKey = {
                privateKey: pair.privateKey, // CryptoKey, sparas som objekt (inte som bytes) i IndexedDB
                publicKeyRaw: new Uint8Array(await crypto.subtle.exportKey("raw", pair.publicKey)) // publik nyckel, 65 bytes
            };
            await dbPut(db, id, deviceKey);  // Sparar det nya nyckelparet. Hit kommer vi bara om ingen nyckel fanns.
            return deviceKey;
        } finally {
            db.close();
        }
    }

    // ----- Nyckelhärledning -----

    // ECDH (Elliptic Curve Diffie-Hellman) ger en delad hemlighet. HKDF (HMAC-based Key Derivation Function)
    // med SHA-256 (Secure Hash Algorithm 256) gör om den till en inslagningsnyckel
    // (AES-GCM (Advanced Encryption Standard – Galois/Counter Mode)-nyckel som låser och låser upp rumsnyckeln).
    async function deriveWrappingKey(privateKey, peerPublicKeyRaw) {
        const peerKey = await crypto.subtle.importKey("raw", peerPublicKeyRaw, CURVE, false, []);
        const sharedSecret = await crypto.subtle.deriveBits({ name: "ECDH", public: peerKey }, privateKey, 256);

        const hkdfKey = await crypto.subtle.importKey("raw", sharedSecret, "HKDF", false, ["deriveKey"]);
        return crypto.subtle.deriveKey(
            { name: "HKDF", hash: "SHA-256", salt: new Uint8Array(0), info: WRAP_INFO },
            hkdfKey,
            { name: "AES-GCM", length: 256 },
            false,
            ["encrypt", "decrypt"]
        );
    }

    // ----- Rumsnycklar -----

    // Skapar en ny rumsnyckel (nyckel som skaparen av ett grupprum slumpar fram) på 256 bitar.
    // raw behövs för att kunna slå in rumsnyckeln åt nya medlemmar.
    async function generateRoomKey() {
        const raw = crypto.getRandomValues(new Uint8Array(32)); // kryptografiskt säker slump
        const key = await crypto.subtle.importKey("raw", raw, "AES-GCM", false, ["encrypt", "decrypt"]);
        return { raw, key };
    }

    // Skapar en inslagen rumsnyckel (rumsnyckeln krypterad åt en viss medlem) enligt
    // ECIES (Elliptic Curve Integrated Encryption Scheme): tillfälligt nyckelpar + mottagarens
    // publika nyckel (öppna delen av enhetsnyckeln) → delad hemlighet → inslagningsnyckel → AES-GCM.
    async function wrapRoomKey(roomKeyRaw, recipientPublicKeyB64) {
        const ephemeral = await crypto.subtle.generateKey(CURVE, false, ["deriveBits"]);
        const wrappingKey = await deriveWrappingKey(ephemeral.privateKey, fromBase64(recipientPublicKeyB64));

        const iv = crypto.getRandomValues(new Uint8Array(12)); // ny IV (Initialization Vector) varje gång
        const wrapped = await crypto.subtle.encrypt({ name: "AES-GCM", iv }, wrappingKey, roomKeyRaw);

        // Den tillfälliga privata nyckeln används inte mer och kan aldrig återskapas
        return {
            wrappedKey: toBase64(wrapped),
            keyIv: toBase64(iv),
            ephemeralPublicKey: toBase64(await crypto.subtle.exportKey("raw", ephemeral.publicKey))
        };
    }

    // Låser upp en inslagen rumsnyckel med min privata nyckel
    // och den tillfälliga publika nyckeln från inslagningen
    async function unwrapRoomKey({ wrappedKey, keyIv, ephemeralPublicKey }, privateKey) {
        const wrappingKey = await deriveWrappingKey(privateKey, fromBase64(ephemeralPublicKey));
        const raw = new Uint8Array(await crypto.subtle.decrypt(
            { name: "AES-GCM", iv: fromBase64(keyIv) }, wrappingKey, fromBase64(wrappedKey)));

        const key = await crypto.subtle.importKey("raw", raw, "AES-GCM", false, ["encrypt", "decrypt"]);
        return { raw, key };
    }

    // ----- Meddelanden -----

    // Krypterar ett meddelande med rumsnyckeln.
    // AAD (Additional Authenticated Data) = rums-id Krypteras inte, men kontrolleras vid dekryptering.
    // Flyttas meddelandet till ett annat rum misslyckas dekrypteringen.
    async function encryptMessage(roomKey, plaintext, roomId) {
        const iv = crypto.getRandomValues(new Uint8Array(12)) // ny IV (Initialization Vector) per meddelande
        const ciphertext = await crypto.subtle.encrypt(
            { name: "AES-GCM", iv, additionalData: new TextEncoder().encode(roomId) },
            roomKey,
            new TextEncoder().encode(plaintext)
        );
        return { ciphertext: toBase64(ciphertext), iv: toBase64(iv) };
    }

    // Dekrypterar. Kastar ett fel om ciphertext, IV (Initialization Vector) eller rums-id (AAD, Additional Authenticated Data) har ändrats.
    async function decryptMessage(roomKey, { ciphertext, iv }, roomId) {
        const plaintext = await crypto.subtle.decrypt(
            { name: "AES-GCM", iv: fromBase64(iv), additionalData: new TextEncoder().encode(roomId) },
            roomKey,
            fromBase64(ciphertext)
        );
        return new TextDecoder().decode(plaintext);
    }

    // Bara dessa funktioner syns utanför modulen
    return {
        toBase64, getOrCreateDeviceKey,
        generateRoomKey, wrapRoomKey, unwrapRoomKey,
        encryptMessage, decryptMessage
    };
})();
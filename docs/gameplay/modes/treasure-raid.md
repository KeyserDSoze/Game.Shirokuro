# Prima modalità PvP — furto del tesoro (nome di lavoro)

**Stato: bozza da confermare.** Fonte: descrizione dell'autore del 09/11/2026, insieme alle grafiche dell'archivio originale. Tracking: [#5](https://github.com/KeyserDSoze/Game.Shirokuro/issues/5). Il nome «furto del tesoro» non è ancora ufficiale.

## Regole comunicate esplicitamente

1. Shirokuro è un gioco su mappa, principalmente PvP, con eroi scelti dai giocatori.
2. Si gioca a squadre; sono possibili più squadre e modalità differenti.
3. In questa modalità una squadra deve **rubare il tesoro di una squadra avversaria** e **riportarlo alla propria base senza morire**.
4. La morte causa una sconfitta/perdita («quando si muore si perde»), ma **non è definito il soggetto della sconfitta**.
5. Ogni personaggio parte dal **livello 1**.
6. Un'eliminazione valida di un avversario fa raggiungere all'eroe il **livello 2** e sblocca un potenziamento importante.
7. Il livello massimo è **2**. Non è previsto un livello 3.
8. I giocatori possono muoversi senza rivelare ad altri la propria destinazione: cosa venga mostrato e quando è ancora da definire.

## Invarianti confermati implementabili

- `HeroProgression.Start()` produce livello 1.
- `HeroProgression.AfterConfirmedKill()` porta livello 1 → livello 2.
- Ulteriori eliminazioni **non** producono livello 3.
- La progressione è associata all'eroe, e il backend deve applicarla **solo dopo** una eliminazione confermata dalle regole del match. Il backend di validazione delle uccisioni non esiste ancora.
- Il potenziamento è indicato come sbloccato al livello 2, ma il suo **effetto non è stato codificato**.

## Domande aperte (non inventare risposte)

| Area | Da chiarire |
|---|---|
| Squadre | Due squadre obbligatorie nella prima modalità o tre/quattro contemporaneamente? Numero di giocatori per squadra? |
| Morte e sconfitta | Muore un eroe ed è eliminato, oppure perde tutta la squadra? Il portatore morto fa perdere subito? È previsto respawn? |
| Tesoro | Punto iniziale, proprietà, trasporto, abbandono/caduta a morte, ripresa, ritorno automatico; condizione precisa di vittoria e se serve il proprio tesoro in base. |
| Movimento | Turni o tempo reale? Uso delle carte di movimento trovate nell'archivio? Quantità massima di spostamento per terreno/eroe? |
| Informazioni | Cosa vedono alleati, avversari, portatore del tesoro, osservatori, giocatori eliminati? Lo spostamento di un clone di Kira viene sempre rivelato? |
| Combattimento | Quali azioni infliggono danni? Quando una kill è valida, chi riceve il livello in caso di assist o effetti ambientali? |
| Abilità | Le tavole `screens2/` contengono descrizioni e simboli: semantica, frequenza, costo e cooldown da verificare. |
| Fine partita | Condizioni di parità, limiti di durata e disconnessione. |

## Casi di test minimi della parte già certa

- Nuovo eroe → livello 1 e potenziamento non ancora sbloccato.
- Prima eliminazione confermata → livello 2 e flag di potenziamento.
- Seconda eliminazione confermata → resta al livello 2.
- Progressione di un eroe → non cambia quella di un altro.

## Sicurezza PvP

Lo stato della partita risiede sul server; i client inviano **intenzioni**, mai una kill, una posizione o una vittoria da accettare come verità. Ogni osservazione viene filtrata **prima di serializzare la risposta**. Non inviare percorsi segreti o destinazioni degli avversari ai client, nemmeno «nascosti» nella UI.

La generazione delle prime schermate, i poteri per eroe, tesori, regole di morte e networking restano bloccati in [#5](https://github.com/KeyserDSoze/Game.Shirokuro/issues/5) fino a conferma delle regole.


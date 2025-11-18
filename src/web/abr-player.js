(function () {
    const video = document.getElementById('v');
    const gearBtn = document.getElementById('gearBtn');
    const qualityMenu = document.getElementById('qualityMenu');
    const spinner = document.getElementById('spinner');

    const params = new URLSearchParams(location.search);
    const itemId = params.get('itemId');
    const mode = params.get('mode') || 'default';

    // Fehler wenn keine ID
    if (!itemId) {
        document.body.innerHTML = '<p style="padding:1rem; color:white;">Fehler: Missing ?itemId=…</p>';
        return;
    }

    // URL bauen (wie im Original)
    const base = location.origin + '/AbrHls/manifest/';
    const m3u8 = mode === 'firetv_sdr' ? base + 'firetv/sdr/' + itemId :
        (mode === 'firetv_hdr' ? base + 'firetv/hdr/' + itemId : base + itemId);

    // Menü Toggle
    gearBtn.addEventListener('click', (e) => {
        e.stopPropagation(); // Verhindert, dass Klick das Menü sofort wieder schließt
        qualityMenu.classList.toggle('show');
    });

    // Menü schließen wenn man woanders klickt
    document.addEventListener('click', (e) => {
        if (!qualityMenu.contains(e.target) && e.target !== gearBtn) {
            qualityMenu.classList.remove('show');
        }
    });

    if (Hls.isSupported()) {
        const hls = new Hls({
            capLevelOnFPSDrop: true,
            startLevel: -1, // Startet im Auto-Modus
            abrEwmaDefaultEstimate: 5e6 // Schätzung für Start-Bandbreite
        });

        hls.loadSource(m3u8);
        hls.attachMedia(video);

        // Wenn die Manifest-Datei geladen ist, wissen wir welche Qualitäten es gibt
        hls.on(Hls.Events.MANIFEST_PARSED, function (event, data) {
            buildQualityMenu(hls);
            video.play().catch(e => console.log("Autoplay blocked, user must click play"));
        });

        // Spinner Logik (Laden/Buffering anzeigen)
        hls.on(Hls.Events.FRAG_LOADING, () => spinner.style.display = 'block');
        hls.on(Hls.Events.FRAG_LOADED, () => spinner.style.display = 'none');
        hls.on(Hls.Events.FRAG_CHANGED, () => spinner.style.display = 'none');

    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
        // Fallback für Safari (iOS), hier funktioniert das manuelle Menü oft nicht, 
        // da iOS das HLS-Handling systemseitig übernimmt.
        video.src = m3u8;
        gearBtn.style.display = 'none'; // Menü ausblenden bei Apple Native Player
    } else {
        document.body.innerHTML = '<p style="padding:1rem">HLS not supported</p>';
    }

    // Funktion zum Bauen des Menüs
    function buildQualityMenu(hls) {
        qualityMenu.innerHTML = ''; // Reset

        // 1. "Automatisch" Option
        const autoBtn = document.createElement('div');
        autoBtn.className = 'quality-option active'; // Standardmäßig aktiv
        autoBtn.innerHTML = 'Automatisch';
        autoBtn.onclick = () => {
            hls.currentLevel = -1; // -1 bedeutet AUTO in hls.js
            updateActiveState(autoBtn);
            qualityMenu.classList.remove('show');
        };
        qualityMenu.appendChild(autoBtn);

        // 2. Verfügbare Stufen (reverse, damit höchste Qualität oben steht)
        // hls.levels enthält die Infos aus der m3u8, die dein C# Code generiert hat
        hls.levels.slice().reverse().forEach((level, index) => {
            // Der Index im reversed Array ist anders, wir brauchen den Original-Index für hls.js
            const originalIndex = hls.levels.length - 1 - index;

            const btn = document.createElement('div');
            btn.className = 'quality-option';

            // Auflösung und Bitrate berechnen
            const height = level.height;
            const bitrate = (level.bitrate / 1000000).toFixed(1) + ' Mbps';

            btn.innerHTML = `${height}p <span>${bitrate}</span>`;

            btn.onclick = () => {
                hls.currentLevel = originalIndex; // Setzt feste Qualität
                updateActiveState(btn);
                qualityMenu.classList.remove('show');
            };
            qualityMenu.appendChild(btn);
        });
    }

    function updateActiveState(selectedBtn) {
        // Alle Buttons deaktivieren
        const options = document.querySelectorAll('.quality-option');
        options.forEach(o => o.classList.remove('active'));
        // Gewählten Button aktivieren
        selectedBtn.classList.add('active');
    }

})();
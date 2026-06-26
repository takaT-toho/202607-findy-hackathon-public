(() => {
    'use strict';

    // 自動更新の間隔（ミリ秒）
    const POLL_INTERVAL_MS = 4000;

    // 画面の各要素への参照
    const clock = document.getElementById('sb-clock');
    const refreshBtn = document.getElementById('refresh-btn');
    const liveIndicator = document.getElementById('live-indicator');
    const liveText = document.getElementById('live-text');
    const alertBanner = document.getElementById('alert-banner');
    const liveLine = document.getElementById('live-line');
    const liveLineSub = document.getElementById('live-line-sub');
    const liveLineBadge = document.getElementById('live-line-badge');
    const liveLineBadgeText = document.getElementById('live-line-badge-text');

    // Date を "H:MM"（24時間表記）の文字列にする。
    function formatTime(date) {
        const h = date.getHours();
        const m = String(date.getMinutes()).padStart(2, '0');
        return `${h}:${m}`;
    }

    // 運行状況から、表示用のバッジ情報（色クラス・ラベル・補足文）を作る。
    function toBadge(status, delayMinutes) {
        switch (status) {
            case 'suspended':
                return { cls: 'badge-error', label: '運転見合わせ', sub: '運転を見合わせています' };
            case 'delayed':
                return {
                    cls: 'badge-delay',
                    label: '遅延',
                    sub: delayMinutes > 0 ? `約${delayMinutes}分の遅れ` : '遅れが発生しています',
                };
            default:
                return { cls: 'badge-normal', label: '平常運転', sub: '遅延情報はありません' };
        }
    }

    // 取得した運行状況を画面に正常表示し、障害表示を解除する。
    function renderNormal(trainStatus) {
        const status = trainStatus?.status ?? 'normal';
        const delay = trainStatus?.delay_minutes ?? 0;
        const badge = toBadge(status, delay);

        liveLine.classList.remove('is-degraded');
        liveLineBadge.className = `line-badge ${badge.cls}`;
        liveLineBadgeText.textContent = badge.label;
        liveLineSub.textContent = badge.sub;

        liveIndicator.classList.remove('degraded');
        liveText.textContent = `自動更新 · ${formatTime(new Date())}`;
        alertBanner.hidden = true;
    }

    // 「取得できません・復旧中」の障害表示にする。エンドユーザーには内部用語を出さない。
    function renderDegraded() {
        liveLine.classList.add('is-degraded');
        liveLineBadge.className = 'line-badge badge-error';
        liveLineBadgeText.textContent = '情報なし';
        liveLineSub.textContent = 'ただいま情報を取得できません · 復旧作業中';

        liveIndicator.classList.add('degraded');
        liveText.textContent = '情報を更新できません · 再試行中';
        alertBanner.hidden = false;
    }

    // サーバーに運行状況を問い合わせて画面を更新する。
    // 200 なら正常表示、それ以外（エラー検知・通信失敗など）は障害表示にする。
    async function poll() {
        try {
            const res = await fetch('/trigger-fetch', { method: 'POST' });
            if (res.status === 200) {
                const body = await res.json().catch(() => null);
                renderNormal(body?.trainStatus);
            } else {
                // エラーの種類によらず、ユーザーには等しく「取得できません」と見せる。
                renderDegraded();
            }
        } catch {
            renderDegraded();
        }
    }

    // ステータスバーの時計を現在時刻に更新する。
    function updateClock() {
        clock.textContent = formatTime(new Date());
    }

    // 更新ボタン: 回転アニメを出して即座に再取得する。
    refreshBtn.addEventListener('click', () => {
        refreshBtn.classList.add('spinning');
        setTimeout(() => refreshBtn.classList.remove('spinning'), 350);
        poll();
    });

    // 起動時の処理: 時計と運行状況を表示し、以降は定期的に自動更新する。
    updateClock();
    poll();
    setInterval(poll, POLL_INTERVAL_MS);
    setInterval(updateClock, 15000);
})();

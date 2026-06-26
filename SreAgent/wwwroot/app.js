// SRE Agent のダッシュボード。/admin/cases を定期取得し、エラーケースを状態別に一覧表示する。
(() => {
    const refreshButton = document.getElementById('refresh-button');
    const autoRefreshCheckbox = document.getElementById('auto-refresh-checkbox');
    const caseList = document.getElementById('case-list');
    const emptyState = document.getElementById('empty-state');
    const summaryTotal = document.getElementById('summary-total');
    const chipAttention = document.getElementById('chip-attention');
    const chipActive = document.getElementById('chip-active');
    const chipDone = document.getElementById('chip-done');
    const attentionBanner = document.getElementById('attention-banner');
    const filters = document.getElementById('filters');

    let timerId = null;
    let lastCases = [];        // 直近に取得したケース（フィルタ切替時の再描画に使う）
    let currentFilter = 'all'; // 絞り込み: all | attention | active | done
    const expanded = new Set(); // 時系列を開いているケースの ID

    // ケースの状態を3つのカテゴリ（要対応・進行中・完了）に振り分ける。
    const ATTENTION = new Set(['Escalated', 'Failed']);
    const DONE = new Set(['PrOpened', 'Rejected']);
    function categoryOf(state) {
        if (ATTENTION.has(state)) return 'attention';
        if (DONE.has(state)) return 'done';
        return 'active';
    }
    const GROUP_RANK = { attention: 0, active: 1, done: 2 };

    // HTML に値を埋め込む前に特殊文字をエスケープする（XSS 対策）。
    function escapeHtml(s) {
        if (s == null) return '';
        return String(s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function formatTimestamp(ts) {
        const d = new Date(ts);
        return d.toLocaleTimeString('ja-JP', { hour12: false }) +
               '.' + String(d.getMilliseconds()).padStart(3, '0');
    }

    function stateLabel(state) {
        const map = {
            Detected: '🔵 検知', Triaged: '🏷 分類済', Rejected: '🚫 棄却',
            Escalated: '⚠️ 要対応', Fixing: '🔧 修正中', Verified: '✅ 検証済',
            Reviewing: '🔍 レビュー中', PrOpened: '🎉 PR作成', Failed: '❌ 失敗'
        };
        return map[state] || state;
    }

    // 1ケースのイベント時系列を HTML 文字列にする。
    function timelineHtml(timeline) {
        return timeline.map(ev => `
            <div class="event event-${escapeHtml(ev.type)}">
                <span class="event-timestamp">${escapeHtml(formatTimestamp(ev.timestamp))}</span>
                <span class="event-type">${escapeHtml(ev.type)}</span>
                <span class="event-summary">${escapeHtml(ev.summary)}</span>
            </div>
        `).join('');
    }

    // 1ケース分のカード（ヘッダ＋折りたたみ時系列）を HTML 文字列にする。
    function caseHtml(c) {
        const open = expanded.has(c.caseId);
        const cls = c.class ? `<span class="badge badge-class">${escapeHtml(c.class)}</span>` : '';
        const conf = c.triageConfidence != null ? `<span class="meta">conf ${c.triageConfidence}</span>` : '';
        const attempts = c.fixAttempts > 0 ? `<span class="meta">fix ×${c.fixAttempts}</span>` : '';
        const pr = c.prUrl ? `<a class="pr-link" href="${escapeHtml(c.prUrl)}" target="_blank">PR ↗</a>` : '';
        const attn = categoryOf(c.state) === 'attention' ? ' case-attention' : '';
        return `
            <div class="case case-state-${escapeHtml(c.state)}${attn}" data-case-id="${escapeHtml(c.caseId)}">
                <div class="case-header" data-toggle="${escapeHtml(c.caseId)}">
                    <span class="case-state">${escapeHtml(stateLabel(c.state))}</span>
                    ${cls}
                    <span class="case-id" title="${escapeHtml(c.caseId)}">${escapeHtml(c.caseId)}</span>
                    ${conf} ${attempts} ${pr}
                    <span class="case-updated">${escapeHtml(formatTimestamp(c.lastUpdated))}</span>
                    <span class="case-caret">${open ? '▾' : '▸'}</span>
                </div>
                <div class="case-timeline" ${open ? '' : 'hidden'}>
                    ${timelineHtml(c.timeline)}
                </div>
            </div>
        `;
    }

    // 要対応 → 進行中 → 完了 の順。同一カテゴリ内は lastUpdated 降順（最近動いた順）。
    function sortForDisplay(cases) {
        return [...cases].sort((a, b) => {
            const rank = GROUP_RANK[categoryOf(a.state)] - GROUP_RANK[categoryOf(b.state)];
            if (rank !== 0) return rank;
            return new Date(b.lastUpdated) - new Date(a.lastUpdated);
        });
    }

    // 取得済みのケースから、サマリー・バナー・一覧をまとめて描画する。
    function render() {
        const counts = { attention: 0, active: 0, done: 0 };
        for (const c of lastCases) counts[categoryOf(c.state)]++;

        summaryTotal.textContent = `${lastCases.length} case${lastCases.length === 1 ? '' : 's'}`;
        chipAttention.textContent = `⚠️ 要対応 ${counts.attention}`;
        chipActive.textContent = `🔧 進行中 ${counts.active}`;
        chipDone.textContent = `🎉 完了 ${counts.done}`;
        chipAttention.classList.toggle('chip-hot', counts.attention > 0);

        // 要対応が1件でもあれば、上部に赤いバナーを出す。
        if (counts.attention > 0) {
            attentionBanner.hidden = false;
            attentionBanner.textContent = `⚠️ ${counts.attention} 件が要対応です（自動修正できず人手が必要 / 失敗）。`;
        } else {
            attentionBanner.hidden = true;
        }

        const visible = currentFilter === 'all'
            ? lastCases
            : lastCases.filter(c => categoryOf(c.state) === currentFilter);

        caseList.querySelectorAll('.case').forEach(el => el.remove());
        if (lastCases.length === 0) {
            emptyState.hidden = false;
            emptyState.textContent = 'Webhook 受信待ち... Target App でエラーを発生させてください。';
            return;
        }
        if (visible.length === 0) {
            emptyState.hidden = false;
            emptyState.textContent = 'この絞り込みに該当するケースはありません。';
            return;
        }
        emptyState.hidden = true;
        caseList.insertAdjacentHTML('beforeend', sortForDisplay(visible).map(caseHtml).join(''));
    }

    // サーバーから最新のケース一覧を取得して再描画する。
    async function refresh() {
        try {
            const response = await fetch('/admin/cases');
            if (!response.ok) return;
            lastCases = await response.json();
            render();
        } catch (err) {
            console.warn('refresh failed', err);
        }
    }

    function setFilter(filter) {
        currentFilter = filter;
        filters.querySelectorAll('.filter').forEach(b =>
            b.classList.toggle('is-active', b.getAttribute('data-filter') === filter));
        render();
    }

    // フィルタボタン / サマリーチップのクリックで絞り込み。
    filters.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-filter]');
        if (btn) setFilter(btn.getAttribute('data-filter'));
    });
    document.getElementById('summary').addEventListener('click', (e) => {
        const chip = e.target.closest('[data-filter]');
        if (chip) setFilter(chip.getAttribute('data-filter'));
    });

    // ケースヘッダのクリックで時系列を開閉する（イベント委譲）。
    caseList.addEventListener('click', (e) => {
        const header = e.target.closest('[data-toggle]');
        if (!header) return;
        const id = header.getAttribute('data-toggle');
        if (expanded.has(id)) expanded.delete(id); else expanded.add(id);
        render();
    });

    // チェックボックスの状態に応じて自動更新を開始/停止する。
    function applyAutoRefresh() {
        if (timerId) { clearInterval(timerId); timerId = null; }
        if (autoRefreshCheckbox.checked) timerId = setInterval(refresh, 3000);
    }

    refreshButton.addEventListener('click', refresh);
    autoRefreshCheckbox.addEventListener('change', applyAutoRefresh);

    refresh();
    applyAutoRefresh();
})();

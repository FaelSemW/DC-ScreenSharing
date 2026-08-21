// ==========================================================================
// DC-ScreenSharing — Admin Management Frontend Logic
// Unified Dual-Protocol Single-Page Publishing (WireGuard + OpenVPN)
// ==========================================================================

let currentActiveSection = 'servers';
let currentKeysList = [];
let currentClientsList = [];
let currentServersList = [];
let currentCredentialSetsList = [];
let currentProtocolTab = 'WIREGUARD';
let keyFilterMode = 'all';
let validationDebounceTimer = null;
let wgValidationDebounceTimer = null;
let bulkFilesToImport = [];
let currentPublicationStatus = null;

const sectionTitles = {
    'dashboard': 'Dashboard',
    'servers': 'Servers (Dual Protocol)',
    'access-keys': 'Access Keys',
    'clients': 'Enrolled Clients',
    'generations': 'Catalog Generations',
    'audit': 'Security & Audit Log',
    'system': 'System Information'
};

document.addEventListener('DOMContentLoaded', async () => {
    setupDragAndDrop();
    await checkSession();
});

window.addEventListener('hashchange', handleHashNavigation);

function handleHashNavigation() {
    const hash = window.location.hash.replace('#', '') || 'servers';
    navigateTo(hash);
}

function navigateTo(sectionId, event) {
    if (event) event.preventDefault();

    const validSections = ['servers', 'dashboard', 'access-keys', 'clients', 'generations', 'audit', 'system'];
    if (!validSections.includes(sectionId)) sectionId = 'servers';

    currentActiveSection = sectionId;
    window.location.hash = sectionId;

    // Update page title in header
    const titleEl = document.getElementById('page-title');
    if (titleEl) {
        titleEl.textContent = sectionTitles[sectionId] || 'Servers';
    }

    // Toggle section visibility
    validSections.forEach(s => {
        const el = document.getElementById(`section-${s}`);
        if (el) el.classList.toggle('hidden', s !== sectionId);
    });

    // Update navigation active states
    document.querySelectorAll('.sidebar-nav .nav-item, .bottom-nav .bottom-nav-item').forEach(item => {
        const href = item.getAttribute('href')?.replace('#', '');
        item.classList.toggle('active', href === sectionId);
    });

    // Load data for section
    try {
        switch (sectionId) {
            case 'servers': loadServers(); break;
            case 'dashboard': loadDashboard(); break;
            case 'access-keys': loadAccessKeys(); break;
            case 'clients': loadClients(); break;
            case 'generations': loadGenerations(); break;
            case 'audit': loadAuditLog(); break;
            case 'system': loadSystemInfo(); break;
        }
    } catch (e) {
        console.error(`Error loading section ${sectionId}:`, e);
    }
}

// ==========================================================================
// AUTHENTICATION & SESSION
// ==========================================================================

async function checkSession() {
    try {
        const res = await fetch('/api/v1/admin/auth/session');
        const data = await res.json();

        if (data.authenticated) {
            document.getElementById('login-view')?.classList.add('hidden');
            document.getElementById('admin-view')?.classList.remove('hidden');
            handleHashNavigation();
        } else {
            showLoginView();
        }
    } catch (err) {
        showLoginView();
    }
}

function showLoginView() {
    document.getElementById('admin-view')?.classList.add('hidden');
    document.getElementById('login-view')?.classList.remove('hidden');
}

async function handleLogin(e) {
    e.preventDefault();
    const btn = document.getElementById('login-btn');
    const errBox = document.getElementById('login-error');
    const apiKey = document.getElementById('admin-api-key').value.trim();

    btn.disabled = true;
    btn.textContent = 'Authenticating...';
    errBox.classList.add('hidden');

    try {
        const res = await fetch('/api/v1/admin/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ apiKey })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            document.getElementById('admin-api-key').value = '';
            document.getElementById('login-view')?.classList.add('hidden');
            document.getElementById('admin-view')?.classList.remove('hidden');
            showToast('Welcome back, Administrator.', 'success');
            handleHashNavigation();
        } else {
            errBox.textContent = data.error || 'Authentication failed. Please verify your admin access key.';
            errBox.classList.remove('hidden');
        }
    } catch (err) {
        errBox.textContent = 'Connection failed. Please ensure the backend service is reachable.';
        errBox.classList.remove('hidden');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Sign In';
    }
}

async function handleLogout() {
    try {
        await fetch('/api/v1/admin/auth/logout', { method: 'POST' });
    } catch { }
    showLoginView();
    showToast('Signed out successfully.', 'info');
}

function togglePasswordVisibility(inputId) {
    const input = document.getElementById(inputId);
    if (!input) return;
    input.type = input.type === 'password' ? 'text' : 'password';
}

// ==========================================================================
// API HELPER & TOASTS
// ==========================================================================

async function apiFetch(url, options = {}) {
    const res = await fetch(url, options);
    if (res.status === 401) {
        showLoginView();
        throw new Error('Unauthorized');
    }
    return res;
}

function showToast(message, type = 'info') {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.textContent = message;
    container.appendChild(toast);

    setTimeout(() => {
        toast.style.opacity = '0';
        toast.style.transform = 'translateY(-10px)';
        toast.style.transition = 'all 0.3s ease';
        setTimeout(() => toast.remove(), 300);
    }, 4000);
}

function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

function formatDate(isoStr) {
    if (!isoStr) return 'Never';
    try {
        const d = new Date(isoStr);
        if (isNaN(d.getTime())) return 'Never';
        return d.toLocaleDateString(undefined, {
            month: 'short',
            day: 'numeric',
            year: 'numeric',
            hour: '2-digit',
            minute: '2-digit'
        });
    } catch {
        return isoStr;
    }
}

// ==========================================================================
// DRAG & DROP HANDLING
// ==========================================================================

function setupDragAndDrop() {
    const ovpnDropzone = document.getElementById('ovpn-dropzone');
    if (ovpnDropzone) {
        ['dragenter', 'dragover'].forEach(name => {
            ovpnDropzone.addEventListener(name, (e) => {
                e.preventDefault();
                e.stopPropagation();
                ovpnDropzone.classList.add('dragover');
            });
        });
        ['dragleave', 'drop'].forEach(name => {
            ovpnDropzone.addEventListener(name, (e) => {
                e.preventDefault();
                e.stopPropagation();
                ovpnDropzone.classList.remove('dragover');
            });
        });
        ovpnDropzone.addEventListener('drop', (e) => {
            const files = e.dataTransfer?.files;
            if (files && files.length > 0) {
                processOvpnFile(files[0]);
            }
        });
    }

    const wgDropzone = document.getElementById('wg-dropzone');
    if (wgDropzone) {
        ['dragenter', 'dragover'].forEach(name => {
            wgDropzone.addEventListener(name, (e) => {
                e.preventDefault();
                e.stopPropagation();
                wgDropzone.classList.add('dragover');
            });
        });
        ['dragleave', 'drop'].forEach(name => {
            wgDropzone.addEventListener(name, (e) => {
                e.preventDefault();
                e.stopPropagation();
                wgDropzone.classList.remove('dragover');
            });
        });
        wgDropzone.addEventListener('drop', (e) => {
            const files = e.dataTransfer?.files;
            if (files && files.length > 0) {
                processWgConfFile(files[0]);
            }
        });
    }

    const bulkDropzone = document.getElementById('bulk-dropzone');
    if (bulkDropzone) {
        ['dragenter', 'dragover'].forEach(name => {
            bulkDropzone.addEventListener(name, (e) => {
                e.preventDefault();
                e.stopPropagation();
                bulkDropzone.classList.add('dragover');
            });
        });
        ['dragleave', 'drop'].forEach(name => {
            bulkDropzone.addEventListener(name, (e) => {
                e.preventDefault();
                e.stopPropagation();
                bulkDropzone.classList.remove('dragover');
            });
        });
        bulkDropzone.addEventListener('drop', (e) => {
            const files = Array.from(e.dataTransfer?.files || []);
            if (files.length > 0) {
                handleBulkFilesDrop(files);
            }
        });
    }
}

// ==========================================================================
// SERVERS & DUAL-PROTOCOL (WIREGUARD + OPENVPN)
// ==========================================================================

function switchProtocolTab(protocol) {
    currentProtocolTab = protocol;

    document.getElementById('tab-btn-wireguard')?.classList.toggle('active', protocol === 'WIREGUARD');
    document.getElementById('tab-btn-openvpn')?.classList.toggle('active', protocol === 'OPENVPN');

    document.getElementById('wg-toolbar')?.classList.toggle('hidden', protocol !== 'WIREGUARD');
    document.getElementById('ovpn-toolbar')?.classList.toggle('hidden', protocol !== 'OPENVPN');

    renderServers();
}

async function loadServers() {
    const container = document.getElementById('servers-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading servers...</div>';

    try {
        const [serversRes, statusRes, credsRes] = await Promise.all([
            apiFetch('/api/v1/admin/servers'),
            apiFetch('/api/v1/admin/servers/publication-status'),
            apiFetch('/api/v1/admin/openvpn/credential-sets')
        ]);
        currentServersList = await serversRes.json();
        currentPublicationStatus = await statusRes.json();
        currentCredentialSetsList = await credsRes.json();

        // Update protocol count badges
        const wgCount = (currentServersList || []).filter(s => (s.protocol || 'WIREGUARD').toUpperCase() === 'WIREGUARD').length;
        const ovpnCount = (currentServersList || []).filter(s => (s.protocol || '').toUpperCase() === 'OPENVPN').length;

        const wgBadge = document.getElementById('wg-count-badge');
        if (wgBadge) wgBadge.textContent = wgCount;

        const ovpnBadge = document.getElementById('ovpn-count-badge');
        if (ovpnBadge) ovpnBadge.textContent = ovpnCount;

        // Update Top Bar Summary Chips
        const topGen = document.getElementById('top-bar-gen');
        if (topGen) {
            topGen.textContent = currentPublicationStatus.activeGeneration > 0 ? `#${currentPublicationStatus.activeGeneration}` : 'None';
        }

        const topPublished = document.getElementById('top-bar-published-count');
        if (topPublished) {
            topPublished.textContent = currentPublicationStatus.activeGenerationCount || 0;
        }

        const topUnpublished = document.getElementById('top-bar-unpublished-count');
        const topUnpublishedChip = document.getElementById('top-bar-unpublished-chip');
        const topPubAllBtn = document.getElementById('top-bar-publish-all-btn');
        const wgPubBtn = document.getElementById('wg-publish-btn');
        const ovpnPubBtn = document.getElementById('ovpn-publish-btn');

        const pendingCount = (currentPublicationStatus.pendingAdditionsCount || 0) +
                             (currentPublicationStatus.pendingModificationsCount || 0) +
                             (currentPublicationStatus.pendingDeletionsCount || 0);

        if (topUnpublished) topUnpublished.textContent = pendingCount;

        if (currentPublicationStatus && currentPublicationStatus.hasPendingChanges) {
            if (topUnpublishedChip) {
                topUnpublishedChip.className = 'chip chip-warning';
            }
            if (topPubAllBtn) topPubAllBtn.classList.remove('hidden');
            if (wgPubBtn) wgPubBtn.classList.remove('hidden');
            if (ovpnPubBtn) ovpnPubBtn.classList.remove('hidden');
        } else {
            if (topUnpublishedChip) {
                topUnpublishedChip.className = 'chip';
            }
            if (topPubAllBtn) topPubAllBtn.classList.add('hidden');
            if (wgPubBtn) wgPubBtn.classList.add('hidden');
            if (ovpnPubBtn) ovpnPubBtn.classList.add('hidden');
        }

        renderServers();
    } catch (e) {
        container.innerHTML = '<div class="empty-state">Unable to load servers.<br><button class="btn btn-secondary btn-sm mt-2" onclick="loadServers()">Retry</button></div>';
    }
}

function renderServers() {
    const container = document.getElementById('servers-container');
    if (!container) return;

    const filtered = (currentServersList || []).filter(s => {
        const p = (s.protocol || 'WIREGUARD').toUpperCase();
        return p === currentProtocolTab;
    });

    if (filtered.length === 0) {
        if (currentProtocolTab === 'WIREGUARD') {
            container.innerHTML = `
                <div class="empty-state">
                    <p style="font-size: 15px; font-weight: 600; color: #F1F5F9; margin-bottom: 6px;">No WireGuard servers configured</p>
                    <p class="text-secondary" style="font-size: 13px; margin-bottom: 16px;">Add a WireGuard server using a .conf configuration file.</p>
                    <button class="btn btn-primary" onclick="openAddServerModal()">+ Add WireGuard Server</button>
                </div>
            `;
        } else {
            container.innerHTML = `
                <div class="empty-state">
                    <p style="font-size: 15px; font-weight: 600; color: #F1F5F9; margin-bottom: 6px;">No OpenVPN servers configured</p>
                    <p class="text-secondary" style="font-size: 13px; margin-bottom: 16px;">Add an OpenVPN server using a .ovpn configuration file.</p>
                    <button class="btn btn-primary" onclick="openAddOpenVpnModal()">+ Add OpenVPN Server</button>
                </div>
            `;
        }
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${filtered.map(s => {
                const isWg = (s.protocol || 'WIREGUARD').toUpperCase() === 'WIREGUARD';
                const providerBadgeClass = s.provider === 'Proton' ? 'badge-proton' : (s.provider === 'VPNBook' ? 'badge-vpnbook' : 'badge-custom');
                const protoBadgeClass = isWg ? 'badge-single' : (s.port === 443 || (s.endpoint && s.endpoint.includes('443')) ? 'badge-tcp' : 'badge-udp');
                const protoLabel = isWg ? 'WIREGUARD' : 'OPENVPN';

                // Credential Set lookup for OpenVPN
                let credSetLabel = '';
                if (!isWg) {
                    if (s.credentialSetId) {
                        const cs = currentCredentialSetsList.find(c => c.id === s.credentialSetId);
                        credSetLabel = cs ? `🔑 Credential Set: ${escapeHtml(cs.name)}` : `🔑 Credential Set: Linked`;
                    } else {
                        credSetLabel = `🔒 Inline / Cert`;
                    }
                }

                // Publication Status Badge
                let pubBadge = '';
                if (!s.enabled) {
                    pubBadge = `<span class="badge badge-revoked">DISABLED</span>`;
                } else if (s.publicationStatus === 'PUBLISHED') {
                    pubBadge = `<span class="badge badge-active">✓ PUBLISHED (GEN #${s.activeGeneration})</span>`;
                } else if (s.publicationStatus === 'PENDING_CHANGES') {
                    pubBadge = `<span class="badge badge-group">⚡ UNPUBLISHED CHANGES</span>`;
                } else {
                    pubBadge = `<span class="badge badge-expired">⚠️ NOT PUBLISHED</span>`;
                }

                const endpointDisplay = s.endpoint ? (s.port ? `${s.endpoint}:${s.port}` : s.endpoint) : 'Endpoint Configured';

                return `
                    <div class="list-row">
                        <div class="row-main">
                            <div class="row-title-line">
                                <span class="row-title">${escapeHtml(s.name)}</span>
                                <span class="badge ${protoBadgeClass}">${protoLabel}</span>
                                <span class="badge ${providerBadgeClass}">${escapeHtml(s.provider || 'Custom')}</span>
                                ${pubBadge}
                            </div>
                            <div class="row-meta">
                                <span>📍 ${escapeHtml(s.country || s.region || 'US')} (${escapeHtml(s.countryCode || 'US')})</span>
                                <span>🌐 ${escapeHtml(endpointDisplay)}</span>
                                ${credSetLabel ? `<span class="badge badge-credset" style="font-size: 10px;">${credSetLabel}</span>` : ''}
                            </div>
                        </div>
                        <div class="row-actions">
                            <button class="btn btn-secondary btn-sm" onclick="editServerModal('${s.serverId}', '${s.protocol}')">Edit</button>
                            ${s.enabled ? `
                                <button class="btn btn-secondary btn-sm" onclick="disableServer('${s.serverId}')">Disable</button>
                            ` : `
                                <button class="btn btn-secondary btn-sm" onclick="enableServer('${s.serverId}')">Enable</button>
                            `}
                            ${(s.publicationStatus === 'NOT_PUBLISHED' || s.publicationStatus === 'PENDING_CHANGES') ? `
                                <button class="btn btn-primary btn-sm btn-publish-highlight" onclick="publishSingleServer('${s.serverId}')" title="Publish Server">⚡ Publish</button>
                            ` : ''}
                            <button class="btn btn-danger btn-sm" onclick="promptDeleteServer('${s.serverId}', '${escapeHtml(s.name)}')">Delete</button>
                        </div>
                    </div>
                `;
            }).join('')}
        </div>
    `;
}

// ==========================================================================
// WIREGUARD MODAL & FORM
// ==========================================================================

function openAddServerModal() {
    document.getElementById('wg-modal-title').textContent = 'Add WireGuard Server';
    document.getElementById('wg-edit-server-id').value = '';
    document.getElementById('server-display-name').value = '';
    document.getElementById('server-country').value = 'United States';
    document.getElementById('server-country-code').value = 'US';
    document.getElementById('server-region').value = '';
    document.getElementById('server-provider').value = 'Custom';
    document.getElementById('server-conf-text').value = '';
    document.getElementById('server-conf-file').value = '';
    document.getElementById('wg-conf-group')?.classList.remove('hidden');
    document.getElementById('wg-validation-box')?.classList.add('hidden');
    document.getElementById('add-server-modal')?.classList.remove('hidden');
}

function closeAddServerModal() {
    document.getElementById('add-server-modal')?.classList.add('hidden');
}

function handleConfFileSelect(e) {
    const file = e.target.files?.[0];
    if (file) processWgConfFile(file);
}

function processWgConfFile(file) {
    const fname = file.name.replace(/\.(conf|txt)$/i, '');
    if (!document.getElementById('server-display-name').value) {
        document.getElementById('server-display-name').value = fname;
    }
    const reader = new FileReader();
    reader.onload = (event) => {
        document.getElementById('server-conf-text').value = event.target.result;
        validateWireGuardLive();
    };
    reader.readAsText(file);
}

function validateWireGuardLive() {
    clearTimeout(wgValidationDebounceTimer);
    wgValidationDebounceTimer = setTimeout(() => {
        const text = document.getElementById('server-conf-text')?.value.trim() || '';
        const box = document.getElementById('wg-validation-box');
        const grid = document.getElementById('wg-val-grid');
        const title = document.getElementById('wg-val-status-title');
        const errMsg = document.getElementById('wg-val-error-msg');

        if (!text) {
            box?.classList.add('hidden');
            return;
        }

        box?.classList.remove('hidden');
        const hasInterface = /\[Interface\]/i.test(text);
        const hasPeer = /\[Peer\]/i.test(text);
        const hasPrivKey = /PrivateKey\s*=/i.test(text);
        const hasPubKey = /PublicKey\s*=/i.test(text);
        const hasEndpoint = /Endpoint\s*=/i.test(text);

        if (hasInterface && hasPeer && hasPrivKey && hasPubKey && hasEndpoint) {
            if (box) box.className = 'validation-preview valid';
            if (title) title.innerHTML = '<span>✅</span> WireGuard Configuration Validated';
            if (errMsg) errMsg.classList.add('hidden');
            if (grid) {
                grid.innerHTML = `
                    <div class="val-item">
                        <div class="val-label">Protocol</div>
                        <div class="val-value"><span class="badge badge-single">WIREGUARD</span></div>
                    </div>
                    <div class="val-item">
                        <div class="val-label">Keys</div>
                        <div class="val-value text-success">Interface &amp; Peer Present</div>
                    </div>
                    <div class="val-item">
                        <div class="val-label">Endpoint</div>
                        <div class="val-value text-success">Configured</div>
                    </div>
                    <div class="val-item">
                        <div class="val-label">Ready to Publish</div>
                        <div class="val-value text-success">YES</div>
                    </div>
                `;
            }
        } else {
            if (box) box.className = 'validation-preview invalid';
            if (title) title.innerHTML = '<span>⚠️</span> Incomplete Configuration';
            if (errMsg) {
                errMsg.textContent = 'Configuration must include [Interface] (with PrivateKey) and [Peer] (with PublicKey and Endpoint).';
                errMsg.classList.remove('hidden');
            }
            if (grid) grid.innerHTML = '';
        }
    }, 200);
}

async function submitWireGuardForm(publishImmediately) {
    const editId = document.getElementById('wg-edit-server-id').value;
    const displayName = document.getElementById('server-display-name').value.trim();
    const country = document.getElementById('server-country').value.trim();
    const countryCode = document.getElementById('server-country-code').value.trim();
    const region = document.getElementById('server-region').value.trim();
    const provider = document.getElementById('server-provider').value.trim();
    const confContent = document.getElementById('server-conf-text').value.trim();

    if (!displayName || !country || (!editId && !confContent)) {
        showToast('Please fill in all required fields.', 'danger');
        return;
    }

    const btn = publishImmediately ? document.getElementById('submit-add-server-publish-btn') : document.getElementById('submit-add-server-save-btn');
    if (btn) {
        btn.disabled = true;
        btn.textContent = publishImmediately ? 'Publishing...' : 'Saving...';
    }

    try {
        let res;
        if (editId) {
            res = await apiFetch(`/api/v1/admin/servers/${editId}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    displayName,
                    region: region || country,
                    enabled: true,
                    publishImmediately
                })
            });
        } else {
            res = await apiFetch('/api/v1/admin/servers', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    displayName,
                    country,
                    countryCode,
                    region: region || country,
                    provider,
                    confContent,
                    publishImmediately
                })
            });
        }

        const data = await res.json();
        if (res.ok && data.success) {
            closeAddServerModal();
            if (publishImmediately && data.published) {
                showPublishSuccessModal(displayName, 'WIREGUARD', data.generation);
            } else {
                showToast(data.message || 'WireGuard server saved.', 'success');
            }
            loadServers();
            loadGenerations();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to save WireGuard server.', 'danger');
        }
    } catch {
        showToast('Error saving WireGuard server.', 'danger');
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.textContent = publishImmediately ? '⚡ SAVE & PUBLISH' : 'Save Draft';
        }
    }
}

// ==========================================================================
// OPENVPN MODAL, LIVE VALIDATION & SAME-PAGE PUBLISHING
// ==========================================================================

async function openAddOpenVpnModal() {
    document.getElementById('ovpn-modal-title').textContent = 'Add OpenVPN Server (.ovpn)';
    document.getElementById('ovpn-edit-server-id').value = '';
    document.getElementById('ovpn-display-name').value = '';
    document.getElementById('ovpn-country').value = 'United States';
    document.getElementById('ovpn-country-code').value = 'US';
    document.getElementById('ovpn-city').value = '';
    document.getElementById('ovpn-provider').value = 'VPNBook';
    document.getElementById('ovpn-file-text').value = '';
    document.getElementById('ovpn-file-input').value = '';
    document.getElementById('ovpn-conf-group')?.classList.remove('hidden');
    document.getElementById('ovpn-validation-box')?.classList.add('hidden');

    await populateCredentialSetsDropdown('ovpn-credential-set');
    document.getElementById('add-openvpn-modal')?.classList.remove('hidden');
}

function closeAddOpenVpnModal() {
    document.getElementById('add-openvpn-modal')?.classList.add('hidden');
}

function handleOvpnFileSelect(e) {
    const file = e.target.files?.[0];
    if (file) processOvpnFile(file);
}

function processOvpnFile(file) {
    const fname = file.name.replace(/\.(ovpn|conf|txt)$/i, '');
    const parts = fname.split(/[-_\s]+/);
    if (parts.length > 0) {
        const code = parts[0].toUpperCase();
        if (code.length === 2) {
            document.getElementById('ovpn-country-code').value = code;
        }
    }

    if (fname.toLowerCase().includes('proton')) {
        document.getElementById('ovpn-provider').value = 'Proton';
    } else if (fname.toLowerCase().includes('vpnbook')) {
        document.getElementById('ovpn-provider').value = 'VPNBook';
    }

    if (!document.getElementById('ovpn-display-name').value) {
        document.getElementById('ovpn-display-name').value = fname;
    }

    const reader = new FileReader();
    reader.onload = (event) => {
        document.getElementById('ovpn-file-text').value = event.target.result;
        validateOvpnLive(false);
    };
    reader.readAsText(file);
}

function handleProviderChange() {
    validateOvpnLive(false);
}

function validateOvpnLive(isManual = false) {
    clearTimeout(validationDebounceTimer);

    const runValidation = async () => {
        const text = document.getElementById('ovpn-file-text')?.value.trim();
        const box = document.getElementById('ovpn-validation-box');
        const grid = document.getElementById('val-grid');
        const title = document.getElementById('val-status-title');
        const errMsg = document.getElementById('val-error-msg');
        const authBadge = document.getElementById('ovpn-auth-badge');

        if (!text || text.length < 10) {
            if (isManual) {
                showToast('Please enter or upload an OpenVPN (.ovpn) configuration first.', 'danger');
            } else {
                box?.classList.add('hidden');
            }
            return;
        }

        const provider = document.getElementById('ovpn-provider')?.value || 'Custom';

        // Check if config contains auth-user-pass
        const hasAuthUserPass = /auth-user-pass/i.test(text);
        if (authBadge) {
            if (hasAuthUserPass) {
                authBadge.textContent = 'Credentials Required (auth-user-pass)';
                authBadge.className = 'badge badge-warning';
            } else {
                authBadge.textContent = 'Cert/Key Only';
                authBadge.className = 'badge badge-subtle';
            }
        }

        try {
            const res = await apiFetch('/api/v1/admin/openvpn/validate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ovpnContent: text, provider })
            });

            const val = await res.json();
            if (box) box.classList.remove('hidden');

            if (val.isValid) {
                if (box) box.className = 'validation-preview valid';
                if (title) title.innerHTML = '<span>✅</span> Safe OpenVPN Profile Validated';
                if (errMsg) errMsg.classList.add('hidden');

                const remotesText = val.additionalRemotesCount > 0 
                    ? `1 Primary + ${val.additionalRemotesCount} Failover` 
                    : '1 Remote Endpoint';

                if (grid) {
                    grid.innerHTML = `
                        <div class="val-item">
                            <div class="val-label">Transport</div>
                            <div class="val-value"><span class="badge ${val.protocol === 'UDP' ? 'badge-udp' : 'badge-tcp'}">${escapeHtml(val.protocol || 'UDP')}</span></div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Primary Remote</div>
                            <div class="val-value">${escapeHtml(val.primaryRemote || 'N/A')}</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Authentication</div>
                            <div class="val-value">${escapeHtml(val.authType || 'TLS')}</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Provider</div>
                            <div class="val-value">${escapeHtml(val.provider || provider)}</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Security Directives</div>
                            <div class="val-value text-success">PASSED</div>
                        </div>
                        <div class="val-item">
                            <div class="val-label">Ready to Publish</div>
                            <div class="val-value text-success">READY</div>
                        </div>
                    `;
                }

                if (isManual) {
                    showToast('OpenVPN configuration validated successfully.', 'success');
                }
            } else {
                if (box) box.className = 'validation-preview invalid';
                if (title) title.innerHTML = '<span>❌</span> Configuration Rejected';
                if (errMsg) {
                    errMsg.textContent = val.error || 'Configuration contains disallowed or dangerous directives.';
                    errMsg.classList.remove('hidden');
                }
                if (grid) grid.innerHTML = '';

                if (isManual) {
                    showToast(val.error || 'Configuration rejected by security policy.', 'danger');
                }
            }
        } catch {
            if (isManual) showToast('Failed to validate profile with server.', 'danger');
        }
    };

    if (isManual) {
        runValidation();
    } else {
        validationDebounceTimer = setTimeout(runValidation, 250);
    }
}

async function submitOpenVpnForm(publishImmediately) {
    const editId = document.getElementById('ovpn-edit-server-id').value;
    const displayName = document.getElementById('ovpn-display-name').value.trim();
    const country = document.getElementById('ovpn-country').value.trim();
    const countryCode = document.getElementById('ovpn-country-code').value.trim();
    const city = document.getElementById('ovpn-city').value.trim();
    const provider = document.getElementById('ovpn-provider').value;
    const credentialSetId = document.getElementById('ovpn-credential-set').value || null;
    const ovpnContent = document.getElementById('ovpn-file-text').value.trim();

    if (!displayName || !country || (!editId && !ovpnContent)) {
        showToast('Please fill in all required fields.', 'danger');
        return;
    }

    const btn = publishImmediately ? document.getElementById('submit-add-ovpn-publish-btn') : document.getElementById('submit-add-ovpn-save-btn');
    if (btn) {
        btn.disabled = true;
        btn.textContent = publishImmediately ? 'Publishing...' : 'Saving...';
    }

    try {
        let res;
        if (editId) {
            res = await apiFetch(`/api/v1/admin/servers/openvpn/${editId}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    displayName,
                    region: city || country,
                    city,
                    provider,
                    credentialSetId,
                    enabled: true,
                    publishImmediately
                })
            });
        } else {
            res = await apiFetch('/api/v1/admin/servers/openvpn', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    displayName,
                    country,
                    countryCode,
                    region: city || country,
                    city,
                    provider,
                    credentialSetId,
                    ovpnContent,
                    publishImmediately
                })
            });
        }

        const data = await res.json();
        if (res.ok && data.success) {
            closeAddOpenVpnModal();
            if (publishImmediately && data.published) {
                showPublishSuccessModal(displayName, 'OPENVPN', data.generation);
            } else {
                showToast(data.message || 'OpenVPN server saved.', 'success');
            }
            loadServers();
            loadGenerations();
            loadDashboard();
            switchProtocolTab('OPENVPN');
        } else {
            showToast(data.error || 'Failed to save OpenVPN server.', 'danger');
        }
    } catch {
        showToast('Error saving OpenVPN server.', 'danger');
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.textContent = publishImmediately ? '⚡ SAVE & PUBLISH' : 'Save Draft';
        }
    }
}

// ==========================================================================
// EDIT SERVER MODAL
// ==========================================================================

async function editServerModal(serverId, protocol) {
    const isWg = (protocol || 'WIREGUARD').toUpperCase() === 'WIREGUARD';
    const server = (currentServersList || []).find(s => s.serverId === serverId);
    if (!server) return;

    if (isWg) {
        document.getElementById('wg-modal-title').textContent = `Edit WireGuard Server`;
        document.getElementById('wg-edit-server-id').value = server.serverId;
        document.getElementById('server-display-name').value = server.name;
        document.getElementById('server-country').value = server.country || 'United States';
        document.getElementById('server-country-code').value = server.countryCode || 'US';
        document.getElementById('server-region').value = server.region || '';
        document.getElementById('server-provider').value = server.provider || 'Custom';
        document.getElementById('wg-conf-group')?.classList.add('hidden');
        document.getElementById('wg-validation-box')?.classList.add('hidden');
        document.getElementById('add-server-modal')?.classList.remove('hidden');
    } else {
        document.getElementById('ovpn-modal-title').textContent = `Edit OpenVPN Server`;
        document.getElementById('ovpn-edit-server-id').value = server.serverId;
        document.getElementById('ovpn-display-name').value = server.name;
        document.getElementById('ovpn-country').value = server.country || 'United States';
        document.getElementById('ovpn-country-code').value = server.countryCode || 'US';
        document.getElementById('ovpn-city').value = server.city || '';
        document.getElementById('ovpn-provider').value = server.provider || 'Custom';
        document.getElementById('ovpn-conf-group')?.classList.add('hidden');
        document.getElementById('ovpn-validation-box')?.classList.add('hidden');

        await populateCredentialSetsDropdown('ovpn-credential-set');
        if (server.credentialSetId) {
            document.getElementById('ovpn-credential-set').value = server.credentialSetId;
        }
        document.getElementById('add-openvpn-modal')?.classList.remove('hidden');
    }
}

// ==========================================================================
// INLINE CREDENTIAL SET CREATION
// ==========================================================================

function openInlineCreateCredSetModal() {
    document.getElementById('inline-cred-name').value = '';
    document.getElementById('inline-cred-provider').value = document.getElementById('ovpn-provider')?.value || 'VPNBook';
    document.getElementById('inline-cred-username').value = '';
    document.getElementById('inline-cred-password').value = '';
    document.getElementById('inline-create-cred-set-modal')?.classList.remove('hidden');
}

function closeInlineCreateCredSetModal() {
    document.getElementById('inline-create-cred-set-modal')?.classList.add('hidden');
}

async function handleSaveInlineCredSet(e) {
    e.preventDefault();
    const btn = document.getElementById('submit-inline-cred-btn');
    btn.disabled = true;
    btn.textContent = 'Saving...';

    const name = document.getElementById('inline-cred-name').value.trim();
    const provider = document.getElementById('inline-cred-provider').value;
    const username = document.getElementById('inline-cred-username').value.trim();
    const password = document.getElementById('inline-cred-password').value;

    try {
        const res = await apiFetch('/api/v1/admin/openvpn/credential-sets', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name, provider, username, password })
        });

        const data = await res.json();
        if (res.ok && data.success && data.credentialSet) {
            closeInlineCreateCredSetModal();
            showToast(`Credential Set "${name}" created.`, 'success');
            await populateCredentialSetsDropdown('ovpn-credential-set');
            document.getElementById('ovpn-credential-set').value = data.credentialSet.id;
        } else {
            showToast(data.error || 'Failed to save credential set.', 'danger');
        }
    } catch {
        showToast('Error saving credential set.', 'danger');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Save Credential Set';
    }
}

// ==========================================================================
// PUBLISH SUCCESS MODAL
// ==========================================================================

function showPublishSuccessModal(serverName, protocol, generation) {
    document.getElementById('pub-success-server-name').textContent = serverName || 'Server';
    const protoBadge = document.getElementById('pub-success-protocol');
    if (protoBadge) {
        protoBadge.textContent = protocol || 'OPENVPN';
        protoBadge.className = protocol === 'WIREGUARD' ? 'badge badge-single' : 'badge badge-warning';
    }
    document.getElementById('pub-success-generation').textContent = `#${generation}`;
    document.getElementById('pub-success-availability').textContent = protocol === 'OPENVPN' 
        ? 'OpenVPN & Dual-Protocol clients' 
        : 'WireGuard & Dual-Protocol clients';
    document.getElementById('publish-success-modal')?.classList.remove('hidden');
}

function closePublishSuccessModal() {
    document.getElementById('publish-success-modal')?.classList.add('hidden');
}

function viewPublishedGeneration() {
    closePublishSuccessModal();
    navigateTo('generations');
}

// ==========================================================================
// PUBLISH ALL / PUBLISH SINGLE SERVER
// ==========================================================================

async function promptPublishAllChanges() {
    if (!confirm('Publish all pending server changes?\n\nThis will atomically compile, sign, and activate a new generation catalog for all compatible clients.')) return;

    try {
        const res = await apiFetch('/api/v1/admin/servers/publish-all', { method: 'POST' });
        const data = await res.json();

        if (res.ok && data.success) {
            showToast(`Generation #${data.generation} published successfully.`, 'success');
            loadServers();
            loadGenerations();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to publish changes.', 'danger');
        }
    } catch {
        showToast('Error publishing changes.', 'danger');
    }
}

async function publishSingleServer(serverId) {
    const s = (currentServersList || []).find(x => x.serverId === serverId);
    const name = s ? s.name : 'Server';

    try {
        const res = await apiFetch(`/api/v1/admin/servers/${serverId}/publish`, { method: 'POST' });
        const data = await res.json();

        if (res.ok && data.success) {
            showPublishSuccessModal(name, s?.protocol || 'OPENVPN', data.generation);
            loadServers();
            loadGenerations();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to publish server.', 'danger');
        }
    } catch {
        showToast('Error publishing server.', 'danger');
    }
}

async function disableServer(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/servers/${id}/disable`, { method: 'POST' });
        if (res.ok) {
            showToast('Server disabled.', 'success');
            loadServers();
            loadDashboard();
        }
    } catch { }
}

async function enableServer(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/servers/${id}/enable`, { method: 'POST' });
        if (res.ok) {
            showToast('Server enabled.', 'success');
            loadServers();
            loadDashboard();
        }
    } catch { }
}

async function promptDeleteServer(id, name) {
    if (confirm(`Remove server "${name}" from registry?`)) {
        try {
            const res = await apiFetch(`/api/v1/admin/servers/${id}`, { method: 'DELETE' });
            if (res.ok) {
                showToast('Server removed.', 'success');
                loadServers();
                loadDashboard();
            }
        } catch { }
    }
}

// ==========================================================================
// BULK OPENVPN IMPORT
// ==========================================================================

function openBulkOpenVpnModal() {
    document.getElementById('bulk-openvpn-modal')?.classList.remove('hidden');
    bulkFilesToImport = [];
    document.getElementById('bulk-files-input').value = '';
    document.getElementById('bulk-preview-container')?.classList.add('hidden');
    populateCredentialSetsDropdown('bulk-cred-set');
}

function closeBulkOpenVpnModal() {
    document.getElementById('bulk-openvpn-modal')?.classList.add('hidden');
}

function handleBulkFilesSelect(e) {
    const files = Array.from(e.target.files || []);
    if (files.length === 0) return;
    handleBulkFilesDrop(files);
}

function handleBulkFilesDrop(files) {
    bulkFilesToImport = files;
    document.getElementById('bulk-files-count').textContent = files.length;
    const list = document.getElementById('bulk-files-list');

    list.innerHTML = files.map(f => `
        <div class="d-flex justify-content-between align-items-center py-1" style="border-bottom: 1px solid var(--border-subtle); font-size: 13px;">
            <span>📄 <strong>${escapeHtml(f.name)}</strong></span>
            <span class="text-muted">${(f.size / 1024).toFixed(1)} KB</span>
        </div>
    `).join('');

    document.getElementById('bulk-preview-container')?.classList.remove('hidden');
}

async function handleBulkImportOpenVpn(e) {
    e.preventDefault();
    if (bulkFilesToImport.length === 0) {
        showToast('Please select at least one .ovpn file to import.', 'danger');
        return;
    }

    const btn = document.getElementById('submit-bulk-ovpn-btn');
    btn.disabled = true;
    btn.textContent = `Importing ${bulkFilesToImport.length} Profiles...`;

    const provider = document.getElementById('bulk-provider').value;
    const credentialSetId = document.getElementById('bulk-cred-set').value || null;

    try {
        const fileReadPromises = bulkFilesToImport.map(file => {
            return new Promise((resolve) => {
                const reader = new FileReader();
                reader.onload = (event) => {
                    const fname = file.name.replace(/\.(ovpn|conf|txt)$/i, '');
                    const parts = fname.split(/[-_\s]+/);
                    let countryCode = 'US';
                    if (parts.length > 0 && parts[0].length === 2) {
                        countryCode = parts[0].toUpperCase();
                    }
                    resolve({
                        displayName: fname,
                        country: countryCode,
                        countryCode: countryCode,
                        region: countryCode,
                        provider,
                        credentialSetId,
                        ovpnContent: event.target.result
                    });
                };
                reader.readAsText(file);
            });
        });

        const servers = await Promise.all(fileReadPromises);

        const res = await apiFetch('/api/v1/admin/servers/openvpn/bulk', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ servers })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            closeBulkOpenVpnModal();
            showToast(data.message || `Imported ${data.importedCount} OpenVPN servers.`, 'success');
            loadServers();
            loadDashboard();
            switchProtocolTab('OPENVPN');
        } else {
            showToast(data.error || 'Bulk import failed.', 'danger');
        }
    } catch {
        showToast('Error executing bulk import.', 'danger');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Import All Profiles';
    }
}

// ==========================================================================
// CREDENTIAL SETS MANAGEMENT MODAL
// ==========================================================================

async function openCredentialSetsModal() {
    document.getElementById('credential-sets-modal')?.classList.remove('hidden');
    closeCredSetForm();
    await loadCredentialSets();
}

function closeCredentialSetsModal() {
    document.getElementById('credential-sets-modal')?.classList.add('hidden');
}

async function loadCredentialSets() {
    const container = document.getElementById('cred-sets-list-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading credential sets...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/openvpn/credential-sets');
        currentCredentialSetsList = await res.json();
        renderCredentialSets();
    } catch {
        container.innerHTML = '<div class="empty-state">Unable to load credential sets.</div>';
    }
}

function renderCredentialSets() {
    const container = document.getElementById('cred-sets-list-container');
    if (!container) return;

    if (!currentCredentialSetsList || currentCredentialSetsList.length === 0) {
        container.innerHTML = '<div class="empty-state">No credential sets created yet. Click "New Credential Set" to add one.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${currentCredentialSetsList.map(cs => `
                <div class="cred-set-card">
                    <div class="cred-set-info">
                        <div class="cred-set-title">
                            🔑 ${escapeHtml(cs.name)}
                            <span class="badge ${cs.provider === 'Proton' ? 'badge-proton' : (cs.provider === 'VPNBook' ? 'badge-vpnbook' : 'badge-custom')}">${escapeHtml(cs.provider)}</span>
                        </div>
                        <div class="cred-set-meta">
                            <span>User: <strong>${escapeHtml(cs.username)}</strong></span>
                            <span>Pass: ••••••••</span>
                            <span>Linked Servers: ${cs.linkedServersCount || 0}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        <button class="btn btn-secondary btn-sm" onclick="editCredSet('${cs.id}')">Edit</button>
                        <button class="btn btn-danger btn-sm" onclick="deleteCredSet('${cs.id}', '${escapeHtml(cs.name)}')">Delete</button>
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

function openCreateCredSetForm() {
    document.getElementById('cred-set-editor-title').textContent = 'New Credential Set';
    document.getElementById('cred-set-edit-id').value = '';
    document.getElementById('cred-set-name').value = '';
    document.getElementById('cred-set-provider').value = 'VPNBook';
    document.getElementById('cred-set-username').value = '';
    document.getElementById('cred-set-password').value = '';
    document.getElementById('cred-set-password').placeholder = 'Enter password';
    document.getElementById('cred-set-editor')?.classList.remove('hidden');
}

function editCredSet(id) {
    const cs = currentCredentialSetsList.find(c => c.id === id);
    if (!cs) return;

    document.getElementById('cred-set-editor-title').textContent = `Edit "${cs.name}"`;
    document.getElementById('cred-set-edit-id').value = cs.id;
    document.getElementById('cred-set-name').value = cs.name;
    document.getElementById('cred-set-provider').value = cs.provider;
    document.getElementById('cred-set-username').value = cs.username;
    document.getElementById('cred-set-password').value = '';
    document.getElementById('cred-set-password').placeholder = 'Leave blank to keep current password';
    document.getElementById('cred-set-editor')?.classList.remove('hidden');
}

function closeCredSetForm() {
    document.getElementById('cred-set-editor')?.classList.add('hidden');
}

async function handleSaveCredSet(e) {
    e.preventDefault();
    const btn = document.getElementById('submit-cred-set-btn');
    btn.disabled = true;
    btn.textContent = 'Saving...';

    const id = document.getElementById('cred-set-edit-id').value;
    const name = document.getElementById('cred-set-name').value.trim();
    const provider = document.getElementById('cred-set-provider').value;
    const username = document.getElementById('cred-set-username').value.trim();
    const password = document.getElementById('cred-set-password').value;

    try {
        let res;
        if (id) {
            res = await apiFetch(`/api/v1/admin/openvpn/credential-sets/${id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, provider, username, password: password || null })
            });
        } else {
            res = await apiFetch('/api/v1/admin/openvpn/credential-sets', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, provider, username, password })
            });
        }

        const data = await res.json();
        if (res.ok && data.success) {
            closeCredSetForm();
            showToast('Credential set saved successfully.', 'success');
            await loadCredentialSets();
        } else {
            showToast(data.error || 'Failed to save credential set.', 'danger');
        }
    } catch {
        showToast('Error saving credential set.', 'danger');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Save Credential Set';
    }
}

async function deleteCredSet(id, name) {
    if (!confirm(`Delete credential set "${name}"?`)) return;

    try {
        const res = await apiFetch(`/api/v1/admin/openvpn/credential-sets/${id}`, { method: 'DELETE' });
        const data = await res.json();
        if (res.ok && data.success) {
            showToast('Credential set deleted.', 'success');
            await loadCredentialSets();
        } else {
            showToast(data.error || 'Cannot delete credential set.', 'danger');
        }
    } catch {
        showToast('Error deleting credential set.', 'danger');
    }
}

async function populateCredentialSetsDropdown(selectId) {
    const sel = document.getElementById(selectId);
    if (!sel) return;

    try {
        const res = await apiFetch('/api/v1/admin/openvpn/credential-sets');
        currentCredentialSetsList = await res.json();
        sel.innerHTML = '<option value="">None / Inline Certificate Only</option>' +
            currentCredentialSetsList.map(c => `<option value="${c.id}">${escapeHtml(c.name)} (${escapeHtml(c.provider)})</option>`).join('');
    } catch { }
}

// ==========================================================================
// GENERATIONS (ADVANCED / HISTORY / ROLLBACK)
// ==========================================================================

async function loadGenerations() {
    const container = document.getElementById('generations-container');
    const hero = document.getElementById('generations-active-hero');
    const pendingBox = document.getElementById('generations-pending-box');
    const pendingList = document.getElementById('generations-pending-list');

    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading generations...</div>';

    try {
        const [genRes, statusRes] = await Promise.all([
            apiFetch('/api/v1/admin/generations'),
            apiFetch('/api/v1/admin/servers/publication-status')
        ]);
        const list = await genRes.json();
        const status = await statusRes.json();

        // Update Active Generation Hero Card
        if (hero) {
            if (status.activeGeneration > 0) {
                hero.innerHTML = `
                    <div style="display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px;">
                        <div>
                            <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 4px;">
                                <span style="font-size: 18px; font-weight: bold; color: #F8FAFC;">Active Generation #${status.activeGeneration}</span>
                                <span class="badge badge-active">ACTIVE &amp; SIGNED</span>
                            </div>
                            <div style="font-size: 12px; color: #94A3B8;">
                                Published: <strong>${status.activePublishedAtUtc ? formatDate(status.activePublishedAtUtc) : 'N/A'}</strong> |
                                Total Active Servers: <strong>${status.activeGenerationCount}</strong>
                            </div>
                        </div>
                        <button class="btn btn-primary btn-sm btn-publish-highlight" onclick="promptPublishAllChanges()">
                            ⚡ Publish New Generation
                        </button>
                    </div>
                `;
            } else {
                hero.innerHTML = `
                    <div style="display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px;">
                        <div>
                            <span style="font-size: 16px; font-weight: bold; color: #F8FAFC;">No Active Generation Published</span>
                            <div style="font-size: 12px; color: #FCD34D;">Clients currently receive zero servers. Publish a generation to make servers live.</div>
                        </div>
                        <button class="btn btn-primary btn-sm btn-publish-highlight" onclick="promptPublishAllChanges()">
                            ⚡ Publish Initial Generation
                        </button>
                    </div>
                `;
            }
        }

        // Update Pending Changes Box
        if (pendingBox && pendingList) {
            if (status.hasPendingChanges) {
                pendingBox.classList.remove('hidden');
                pendingList.innerHTML = (status.pendingChangesSummary || []).map(item => `<div style="padding: 2px 0;">${escapeHtml(item)}</div>`).join('');
            } else {
                pendingBox.classList.add('hidden');
            }
        }

        if (!list || list.length === 0) {
            container.innerHTML = '<div class="empty-state">No signed catalog generations found. Click "Compile & Publish New Generation" to publish.</div>';
            return;
        }

        container.innerHTML = `
            <div class="item-list">
                ${list.map(g => `
                    <div class="list-row">
                        <div class="row-main">
                            <div class="row-title-line">
                                <span class="row-title">Generation #${g.generation}</span>
                                <span class="badge ${g.isActive ? 'badge-active' : 'badge-expired'}">${g.isActive ? 'Active Generation' : 'Archived'}</span>
                                <span class="badge badge-accent">Total: ${g.serverCount}</span>
                                <span class="badge badge-single">WG: ${(g.wireGuardCount !== undefined ? g.wireGuardCount : g.wireguardCount) || 0}</span>
                                <span class="badge badge-warning">OVPN: ${(g.openVpnCount !== undefined ? g.openVpnCount : g.openvpnCount) || 0}</span>
                            </div>
                            <div class="row-meta">
                                <span>Published: ${formatDate(g.publishedAtUtc)}</span>
                                <span>By: ${escapeHtml(g.publishedBy || 'Admin')}</span>
                            </div>
                        </div>
                        <div class="row-actions">
                            ${!g.isActive ? `
                                <button class="btn btn-secondary btn-sm" onclick="rollbackToGeneration(${g.generation})">Rollback to #${g.generation}</button>
                            ` : ''}
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    } catch {
        container.innerHTML = '<div class="empty-state">Unable to load catalog generations.</div>';
    }
}

async function rollbackToGeneration(genNumber) {
    if (!confirm(`Rollback active catalog to Generation #${genNumber}?\n\nClients will immediately begin receiving profiles from Generation #${genNumber}.`)) return;

    try {
        const res = await apiFetch(`/api/v1/admin/generations/${genNumber}/publish`, { method: 'POST' });
        const data = await res.json();
        if (res.ok && data.success) {
            showToast(`Active catalog switched to Generation #${genNumber}.`, 'success');
            loadGenerations();
            loadServers();
            loadDashboard();
        } else {
            showToast(data.error || 'Rollback failed.', 'danger');
        }
    } catch {
        showToast('Error executing rollback.', 'danger');
    }
}

// ==========================================================================
// DASHBOARD VIEW
// ==========================================================================

async function loadDashboard() {
    try {
        const res = await apiFetch('/api/v1/admin/dashboard');
        const data = await res.json();

        // Check publication status for warning banner
        const statusRes = await apiFetch('/api/v1/admin/servers/publication-status');
        const status = await statusRes.json();

        const banner = document.getElementById('dashboard-unpublished-banner');
        if (banner) {
            banner.classList.toggle('hidden', !status.hasPendingChanges);
        }

        // Metrics
        const genEl = document.getElementById('metric-generation');
        if (genEl) genEl.textContent = data.currentGeneration > 0 ? `Gen #${data.currentGeneration}` : 'None';

        const pubServersEl = document.getElementById('metric-published-servers');
        if (pubServersEl) pubServersEl.textContent = status.activeGenerationCount || 0;

        const wgServersEl = document.getElementById('metric-wg-servers');
        if (wgServersEl) wgServersEl.textContent = data.wireGuardServersCount || 0;

        const ovpnServersEl = document.getElementById('metric-ovpn-servers');
        if (ovpnServersEl) ovpnServersEl.textContent = data.openVpnServersCount || 0;

        const clientsEl = document.getElementById('metric-active-clients');
        if (clientsEl) clientsEl.textContent = data.activeClientsCount || 0;

        const keysEl = document.getElementById('metric-active-keys');
        if (keysEl) keysEl.textContent = data.activeKeysCount || 0;

        // Recent Activations
        const actContainer = document.getElementById('dashboard-activations');
        if (actContainer) {
            if (!data.recentActivations || data.recentActivations.length === 0) {
                actContainer.innerHTML = '<div class="empty-state">No client activations recorded yet.</div>';
            } else {
                actContainer.innerHTML = `
                    <div class="item-list">
                        ${data.recentActivations.map(c => `
                            <div class="list-row">
                                <div class="row-main">
                                    <div class="row-title-line">
                                        <span class="row-title"><code>${escapeHtml((c.clientId || '').substring(0, 16))}...</code></span>
                                        <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                                        <span class="badge badge-single">${escapeHtml(c.accessKeyName || 'Ticket')}</span>
                                    </div>
                                    <div class="row-meta">
                                        <span>Enrolled: ${formatDate(c.enrolledAtUtc)}</span>
                                    </div>
                                </div>
                            </div>
                        `).join('')}
                    </div>
                `;
            }
        }

        // Recent Audit
        const auditContainer = document.getElementById('dashboard-audit');
        if (auditContainer) {
            if (!data.recentAudit || data.recentAudit.length === 0) {
                auditContainer.innerHTML = '<div class="empty-state">No security events recorded.</div>';
            } else {
                auditContainer.innerHTML = `
                    <div class="item-list">
                        ${data.recentAudit.map(a => `
                            <div class="list-row">
                                <div class="row-main">
                                    <div class="row-title-line">
                                        <span class="row-title">${escapeHtml(a.eventType)}</span>
                                        <span class="text-muted" style="font-size: 11px;">IP: ${escapeHtml(a.ipAddress)}</span>
                                    </div>
                                    <div class="row-meta">
                                        <span>Actor: ${escapeHtml(a.actor)}</span>
                                        <span>${formatDate(a.timestampUtc)}</span>
                                    </div>
                                </div>
                            </div>
                        `).join('')}
                    </div>
                `;
            }
        }
    } catch { }
}

// ==========================================================================
// ACCESS KEYS
// ==========================================================================

async function loadAccessKeys() {
    const container = document.getElementById('access-keys-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading access keys...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/access-keys');
        currentKeysList = await res.json();
        renderAccessKeys();
    } catch {
        container.innerHTML = '<div class="empty-state">Unable to load access keys.</div>';
    }
}

function renderAccessKeys() {
    const container = document.getElementById('access-keys-container');
    if (!container) return;

    if (!currentKeysList || currentKeysList.length === 0) {
        container.innerHTML = '<div class="empty-state">No access keys found. Click "Generate Access Key" to create one.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${currentKeysList.map(k => `
                <div class="list-row">
                    <div class="row-main">
                        <div class="row-title-line">
                            <span class="row-title">${escapeHtml(k.name)}</span>
                            <span class="badge ${k.status === 'Active' ? 'badge-active' : 'badge-revoked'}">${escapeHtml(k.status)}</span>
                            <span class="badge ${k.type === 'GROUP' ? 'badge-group' : 'badge-single'}">${escapeHtml(k.type)}</span>
                        </div>
                        <div class="row-meta">
                            <span>Prefix: <code>${escapeHtml(k.codePrefix)}...</code></span>
                            <span>Uses: ${k.usesCount}${k.maxUses ? ` / ${k.maxUses}` : ' (Unlimited)'}</span>
                            <span>Expires: ${formatDate(k.expiresAtUtc)}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        <button class="btn btn-secondary btn-sm" onclick="showKeyUsage('${k.id}')">Activations</button>
                        ${k.status === 'Active' ? `
                            <button class="btn btn-danger btn-sm" onclick="revokeAccessKey('${k.id}')">Revoke</button>
                        ` : ''}
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

function openGenerateKeyModal() {
    document.getElementById('key-name').value = '';
    document.getElementById('key-type').value = 'SINGLE_USE';
    document.getElementById('key-expiration').value = '30d';
    document.getElementById('key-max-uses').value = '';
    document.getElementById('group-key-options')?.classList.add('hidden');
    document.getElementById('key-custom-expiration')?.classList.add('hidden');
    document.getElementById('generate-key-modal')?.classList.remove('hidden');
}

function closeGenerateKeyModal() {
    document.getElementById('generate-key-modal')?.classList.add('hidden');
}

function toggleKeyTypeOptions() {
    const isGroup = document.getElementById('key-type').value === 'GROUP';
    document.getElementById('group-key-options')?.classList.toggle('hidden', !isGroup);
}

function toggleCustomExpiration() {
    const isCustom = document.getElementById('key-expiration').value === 'custom';
    document.getElementById('key-custom-expiration')?.classList.toggle('hidden', !isCustom);
}

async function handleGenerateKey(e) {
    e.preventDefault();
    const btn = document.getElementById('submit-gen-key-btn');
    btn.disabled = true;
    btn.textContent = 'Generating...';

    const name = document.getElementById('key-name').value.trim();
    const type = document.getElementById('key-type').value;
    const expiration = document.getElementById('key-expiration').value;
    const customDate = document.getElementById('key-custom-expiration').value;
    const maxUses = document.getElementById('key-max-uses').value;

    try {
        const res = await apiFetch('/api/v1/admin/access-keys', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                name,
                type,
                expiration,
                customExpiresAtUtc: customDate ? new Date(customDate).toISOString() : null,
                maxUses: maxUses ? parseInt(maxUses, 10) : null
            })
        });

        const data = await res.json();
        if (res.ok && data.success) {
            closeGenerateKeyModal();
            promptCopyKey(data.accessKey || data.plaintextCode, name);
            loadAccessKeys();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to generate key.', 'danger');
        }
    } catch {
        showToast('Error generating access key.', 'danger');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Generate Key';
    }
}

function promptCopyKey(keyCode, keyName) {
    navigator.clipboard?.writeText(keyCode).catch(() => {});
    alert(`Access Key Generated Successfully!\n\nName: ${keyName}\nKey Code: ${keyCode}\n\n(Key copied to clipboard. Store securely - this code will not be shown again.)`);
}

async function revokeAccessKey(id) {
    if (!confirm('Revoke this access key? New clients will not be able to activate with it.')) return;

    try {
        const res = await apiFetch(`/api/v1/admin/access-keys/${id}/revoke`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ revokeClients: false })
        });
        const data = await res.json();
        if (res.ok && data.success) {
            showToast('Access key revoked.', 'success');
            loadAccessKeys();
            loadDashboard();
        } else {
            showToast(data.error || 'Failed to revoke key.', 'danger');
        }
    } catch {
        showToast('Error revoking access key.', 'danger');
    }
}

async function showKeyUsage(id) {
    try {
        const res = await apiFetch(`/api/v1/admin/access-keys/${id}/usage`);
        const data = await res.json();
        const modal = document.getElementById('key-usage-modal');
        const body = document.getElementById('key-usage-modal-body');
        document.getElementById('key-usage-modal-title').textContent = `Activations for "${data.key?.name || 'Key'}"`;

        if (!data.clients || data.clients.length === 0) {
            body.innerHTML = '<div class="empty-state">No clients have activated using this key yet.</div>';
        } else {
            body.innerHTML = `
                <div class="item-list">
                    ${data.clients.map(c => `
                        <div class="list-row">
                            <div class="row-main">
                                <div class="row-title-line">
                                    <span class="row-title"><code>${escapeHtml(c.clientId)}</code></span>
                                    <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                                </div>
                                <div class="row-meta">
                                    <span>IP: ${escapeHtml(c.registeredIp || 'Unknown')}</span>
                                    <span>Enrolled: ${formatDate(c.enrolledAtUtc)}</span>
                                </div>
                            </div>
                        </div>
                    `).join('')}
                </div>
            `;
        }
        modal.classList.remove('hidden');
    } catch { }
}

function closeKeyUsageModal() {
    document.getElementById('key-usage-modal')?.classList.add('hidden');
}

// ==========================================================================
// CLIENTS
// ==========================================================================

async function loadClients() {
    const container = document.getElementById('clients-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading enrolled clients...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/clients');
        currentClientsList = await res.json();
        filterClients();
    } catch {
        container.innerHTML = '<div class="empty-state">Unable to load enrolled clients.</div>';
    }
}

function filterClients() {
    const container = document.getElementById('clients-container');
    if (!container) return;

    const search = (document.getElementById('client-search')?.value || '').toLowerCase().trim();
    const status = document.getElementById('client-status-filter')?.value || '';

    let filtered = (currentClientsList || []).filter(c => {
        if (status === 'active' && !c.isActive) return false;
        if (status === 'revoked' && c.isActive) return false;
        if (search) {
            const matchId = (c.clientId || '').toLowerCase().includes(search);
            const matchIp = (c.registeredIp || '').toLowerCase().includes(search);
            const matchKey = (c.accessKeyName || '').toLowerCase().includes(search);
            if (!matchId && !matchIp && !matchKey) return false;
        }
        return true;
    });

    if (filtered.length === 0) {
        container.innerHTML = '<div class="empty-state">No enrolled clients match the current filter.</div>';
        return;
    }

    container.innerHTML = `
        <div class="item-list">
            ${filtered.map(c => `
                <div class="list-row">
                    <div class="row-main">
                        <div class="row-title-line">
                            <span class="row-title"><code>${escapeHtml((c.clientId || '').substring(0, 16))}...</code></span>
                            <span class="badge ${c.isActive ? 'badge-active' : 'badge-revoked'}">${c.isActive ? 'Active' : 'Revoked'}</span>
                            <span class="badge badge-single">${escapeHtml(c.accessKeyName || 'Direct Ticket')}</span>
                        </div>
                        <div class="row-meta">
                            <span>IP: ${escapeHtml(c.registeredIp || 'Unknown')}</span>
                            <span>Enrolled: ${formatDate(c.enrolledAtUtc)}</span>
                        </div>
                    </div>
                    <div class="row-actions">
                        ${c.isActive ? `
                            <button class="btn btn-danger btn-sm" onclick="revokeClient('${c.clientId}')">Revoke</button>
                        ` : `
                            <button class="btn btn-secondary btn-sm" onclick="restoreClient('${c.clientId}')">Restore</button>
                        `}
                    </div>
                </div>
            `).join('')}
        </div>
    `;
}

async function revokeClient(clientId) {
    try {
        const res = await apiFetch(`/api/v1/admin/clients/${clientId}/revoke`, { method: 'POST' });
        if (res.ok) {
            showToast('Client revoked.', 'success');
            loadClients();
            loadDashboard();
        }
    } catch { }
}

async function restoreClient(clientId) {
    try {
        const res = await apiFetch(`/api/v1/admin/clients/${clientId}/restore`, { method: 'POST' });
        if (res.ok) {
            showToast('Client restored.', 'success');
            loadClients();
            loadDashboard();
        }
    } catch { }
}

// ==========================================================================
// AUDIT LOG & SYSTEM INFO
// ==========================================================================

async function loadAuditLog() {
    const container = document.getElementById('audit-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading audit events...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/audit?limit=100');
        const events = await res.json();

        if (!events || events.length === 0) {
            container.innerHTML = '<div class="empty-state">No audit log entries recorded.</div>';
            return;
        }

        container.innerHTML = `
            <div class="item-list">
                ${events.map(a => `
                    <div class="list-row">
                        <div class="row-main">
                            <div class="row-title-line">
                                <span class="row-title">${escapeHtml(a.eventType)}</span>
                                <span class="badge badge-subtle">${escapeHtml(a.actor || 'System')}</span>
                            </div>
                            <div class="row-meta">
                                <span>IP: ${escapeHtml(a.ipAddress || 'Internal')}</span>
                                ${a.targetId ? `<span>Target: <code>${escapeHtml(a.targetId)}</code></span>` : ''}
                                <span>${formatDate(a.timestampUtc)}</span>
                            </div>
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    } catch {
        container.innerHTML = '<div class="empty-state">Unable to load audit log.</div>';
    }
}

async function loadSystemInfo() {
    const container = document.getElementById('system-container');
    if (!container) return;
    container.innerHTML = '<div class="loading-state">Loading system info...</div>';

    try {
        const res = await apiFetch('/api/v1/admin/system/info');
        const data = await res.json();

        const uptimeHours = (data.uptimeSeconds / 3600).toFixed(1);
        const memMB = (data.memoryBytes / (1024 * 1024)).toFixed(1);

        container.innerHTML = `
            <div class="metrics-grid">
                <div class="metric-card">
                    <div class="metric-value text-success">Online</div>
                    <div class="metric-label">Service Health</div>
                </div>
                <div class="metric-card">
                    <div class="metric-value">${uptimeHours} hrs</div>
                    <div class="metric-label">System Uptime</div>
                </div>
                <div class="metric-card">
                    <div class="metric-value">${memMB} MB</div>
                    <div class="metric-label">Memory Usage</div>
                </div>
                <div class="metric-card">
                    <div class="metric-value">${data.processorCount}</div>
                    <div class="metric-label">CPU Cores</div>
                </div>
            </div>
            <div class="card mt-3 p-3" style="font-size: 13px; color: #94A3B8;">
                <p><strong>OS:</strong> ${escapeHtml(data.osVersion)}</p>
                <p class="mt-1"><strong>Runtime:</strong> .NET ${escapeHtml(data.runtime)} (${data.is64Bit ? '64-bit' : '32-bit'})</p>
                <p class="mt-1"><strong>Machine Name:</strong> ${escapeHtml(data.machineName)}</p>
            </div>
        `;
    } catch {
        container.innerHTML = '<div class="empty-state">Unable to load system info.</div>';
    }
}

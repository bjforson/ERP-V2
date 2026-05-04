// NickHR WebApp - Client-side helpers

// Idle timeout - auto logout after 15 minutes of inactivity
(function () {
    var IDLE_TIMEOUT = 15 * 60 * 1000; // 15 minutes in ms
    var WARNING_BEFORE = 60 * 1000; // Show warning 1 minute before
    var idleTimer = null;
    var warningTimer = null;
    var warningShown = false;
    var warningDiv = null;

    function resetIdleTimer() {
        if (warningDiv && warningShown) {
            warningDiv.remove();
            warningShown = false;
        }
        clearTimeout(idleTimer);
        clearTimeout(warningTimer);

        // Don't set timeout on login page
        if (window.location.pathname === '/login' || window.location.pathname === '/login-callback') return;

        warningTimer = setTimeout(showWarning, IDLE_TIMEOUT - WARNING_BEFORE);
        idleTimer = setTimeout(doLogout, IDLE_TIMEOUT);
    }

    function showWarning() {
        if (warningShown) return;
        warningShown = true;
        warningDiv = document.createElement('div');
        warningDiv.id = 'idle-warning';
        warningDiv.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:99999;background:#F57F17;color:#000;text-align:center;padding:12px;font-size:14px;font-weight:600;box-shadow:0 2px 8px rgba(0,0,0,0.3);';
        warningDiv.innerHTML = '⚠ You will be logged out in 1 minute due to inactivity. Move your mouse or press a key to stay logged in.';
        document.body.appendChild(warningDiv);
    }

    function doLogout() {
        window.location.href = '/logout';
    }

    // Track user activity
    ['mousemove', 'mousedown', 'keypress', 'keydown', 'scroll', 'touchstart', 'click'].forEach(function (evt) {
        document.addEventListener(evt, resetIdleTimer, { passive: true });
    });

    // Start the timer
    resetIdleTimer();
})();

// GPS Location for login geo-fencing
window.getGPSLocation = function () {
    return new Promise((resolve) => {
        if (!navigator.geolocation) {
            resolve({ success: false, error: 'Geolocation not supported by browser' });
            return;
        }
        navigator.geolocation.getCurrentPosition(
            (position) => {
                resolve({
                    success: true,
                    latitude: position.coords.latitude,
                    longitude: position.coords.longitude,
                    accuracy: position.coords.accuracy
                });
            },
            (error) => {
                let msg = 'Unknown error';
                switch (error.code) {
                    case 1: msg = 'Permission denied'; break;
                    case 2: msg = 'Position unavailable'; break;
                    case 3: msg = 'Request timed out'; break;
                }
                resolve({ success: false, error: msg });
            },
            { enableHighAccuracy: true, timeout: 10000, maximumAge: 60000 }
        );
    });
};

window.downloadFile = function (fileName, mimeType, content) {
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

window.downloadFileFromBase64 = function (base64, fileName, mimeType) {
    const byteCharacters = atob(base64);
    const byteNumbers = new Array(byteCharacters.length);
    for (let i = 0; i < byteCharacters.length; i++) {
        byteNumbers[i] = byteCharacters.charCodeAt(i);
    }
    const byteArray = new Uint8Array(byteNumbers);
    const blob = new Blob([byteArray], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};

window.playAudioFromBase64 = (elementId, base64) => {
    const audio = document.getElementById(elementId);
    if (!audio) return;
    audio.src = "data:audio/wav;base64," + base64;
    audio.load();
    audio.play();
};

window.setAudioSrc = (elementId, base64) => {
    const audio = document.getElementById(elementId);
    if (!audio) return;
    audio.src = "data:audio/wav;base64," + base64;
    audio.load();
};

// ── Streaming audio queue ────────────────────────────────────────────────────
// Sentences are synthesised one-by-one on the server and enqueued here.
// Each finishes playing before the next starts automatically.

const _queue = [];
let _isPlaying = false;
let _dotnetRef = null;
let _currentAudio = null;

function _playNext() {
    if (_queue.length === 0) {
        _isPlaying = false;
        _currentAudio = null;
        if (_dotnetRef) _dotnetRef.invokeMethodAsync('OnQueueEmpty');
        return;
    }
    _isPlaying = true;
    const base64 = _queue.shift();
    _currentAudio = new Audio("data:audio/wav;base64," + base64);
    _currentAudio.onended = _playNext;
    _currentAudio.onerror = _playNext;
    _currentAudio.play();
}

window.audioQueueInit = (dotnetRef) => {
    _dotnetRef = dotnetRef;
    _queue.length = 0;
    _isPlaying = false;
    _currentAudio = null;
};

window.audioQueueEnqueue = (base64) => {
    _queue.push(base64);
    if (!_isPlaying) _playNext();
};

window.audioQueueClear = () => {
    _queue.length = 0;
    if (_currentAudio) {
        _currentAudio.onended = null;
        _currentAudio.onerror = null;
        _currentAudio.pause();
        _currentAudio.src = '';
        _currentAudio = null;
    }
    _isPlaying = false;
    _dotnetRef = null;
};

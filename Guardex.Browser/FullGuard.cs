namespace Guardex.Browser;

// A video may enter full screen after a click. A page may not lock Escape or take the screen back by itself.
static class FullGuard
{
	public const int RapidMs = 600;
	public const int PauseSeconds = 20;

	public const string Leave =
		"""
		(() => {
		  const visit = (doc) => {
		    if (!doc) return;
		    try { if (doc.fullscreenElement) doc.exitFullscreen(); } catch (e) {}
		    try { if (doc.pointerLockElement) doc.exitPointerLock(); } catch (e) {}
		    let frames;
		    try { frames = doc.querySelectorAll('iframe,frame'); } catch (e) { return; }
		    for (const frame of frames) {
		      try { visit(frame.contentDocument); } catch (e) {}
		    }
		  };
		  visit(document);
		})()
		""";

	public const string Script =
		"""
		(() => {
		  const grantMs = 1500;
		  let until = 0;
		  let armed = false;
		  let leaving = false;
		  const grant = (event) => {
		    if (event && event.type === 'keydown' && event.key === 'Escape') return;
		    until = Date.now() + grantMs;
		  };
		  window.addEventListener('pointerdown', grant, true);
		  window.addEventListener('keydown', grant, true);
		  const deny = () => Promise.reject(new DOMException('Full screen needs a click on the page.', 'NotAllowedError'));
		  const wrap = (proto, name) => {
		    if (!proto) return;
		    const native = proto[name];
		    if (typeof native !== 'function') return;
		    const gated = function (options) {
		      if (Date.now() > until) return deny();
		      until = 0;
		      armed = true;
		      let result;
		      try {
		        if (options && typeof options === 'object') {
		          const clean = Object.assign({}, options);
		          delete clean.keyboardLock;
		          result = native.call(this, clean);
		        } else result = native.call(this);
		      } catch (e) {
		        armed = false;
		        throw e;
		      }
		      if (result && typeof result.then === 'function') result.then(() => {}, () => { armed = false; });
		      return result;
		    };
		    try {
		      Object.defineProperty(proto, name, { configurable: false, enumerable: true, writable: false, value: gated });
		    } catch (e) {}
		  };
		  wrap(Element.prototype, 'requestFullscreen');
		  wrap(Element.prototype, 'webkitRequestFullscreen');
		  wrap(Element.prototype, 'webkitRequestFullScreen');
		  wrap(Element.prototype, 'mozRequestFullScreen');
		  const exitNative = Document.prototype.exitFullscreen;
		  const exitPointer = Document.prototype.exitPointerLock;
		  const leave = () => {
		    if (leaving) return;
		    leaving = true;
		    until = 0;
		    armed = false;
		    try { if (typeof exitPointer === 'function') exitPointer.call(document); } catch (e) {}
		    try { if (typeof exitNative === 'function') exitNative.call(document); } catch (e) {}
		    setTimeout(() => { leaving = false; }, 0);
		  };
		  if (typeof exitNative === 'function') {
		    try {
		      Object.defineProperty(Document.prototype, 'exitFullscreen', {
		        configurable: false, enumerable: true, writable: false,
		        value: function () { return exitNative.apply(this, arguments); }
		      });
		    } catch (e) {}
		  }
		  const blockLock = (obj) => {
		    if (!obj || typeof obj.lock !== 'function') return;
		    try {
		      Object.defineProperty(obj, 'lock', {
		        configurable: false, enumerable: true, writable: false,
		        value: function () { return Promise.reject(new DOMException('Keyboard lock is off.', 'NotAllowedError')); }
		      });
		    } catch (e) {}
		  };
		  try { blockLock(navigator.keyboard); } catch (e) {}
		  try { blockLock(Object.getPrototypeOf(navigator.keyboard)); } catch (e) {}
		  window.addEventListener('keydown', (event) => {
		    if (event.key !== 'Escape') return;
		    const on = document.fullscreenElement || document.webkitFullscreenElement || document.pointerLockElement;
		    if (!on) return;
		    leave();
		    event.preventDefault();
		    event.stopImmediatePropagation();
		  }, true);
		  document.addEventListener('fullscreenchange', () => {
		    if (document.fullscreenElement) {
		      if (armed) { armed = false; return; }
		      leave();
		      return;
		    }
		    until = 0;
		    armed = false;
		  }, true);
		})()
		""";

	public static string SelfCheck()
	{
		var failures = new List<string>();
		if (!Script.Contains("requestFullscreen", StringComparison.Ordinal) || !Script.Contains("keyboardLock", StringComparison.Ordinal))
			failures.Add("wrap");
		if (!Script.Contains("configurable: false", StringComparison.Ordinal)) failures.Add("lock");
		if (!Script.Contains("Keyboard lock is off", StringComparison.Ordinal) || !Script.Contains("navigator.keyboard", StringComparison.Ordinal))
			failures.Add("keys");
		if (!Script.Contains("event.key !== 'Escape'", StringComparison.Ordinal) || !Script.Contains("stopImmediatePropagation", StringComparison.Ordinal))
			failures.Add("esc");
		if (!Script.Contains("fullscreenchange", StringComparison.Ordinal) || !Script.Contains("armed", StringComparison.Ordinal))
			failures.Add("again");
		if (!Leave.Contains("exitFullscreen", StringComparison.Ordinal) || !Leave.Contains("exitPointerLock", StringComparison.Ordinal))
			failures.Add("leave");
		return failures.Count == 0 ? "PASS" : string.Join("; ", failures);
	}
}

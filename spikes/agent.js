// NativeMods spike agent. Loaded into the client by spikehost (Linux/Proton) with HOST = {base, overlay}.
// Experimental: proves the client's own UI system can be driven from outside. Addresses are image VAs read from the
// 3.29.3d client and used only on it; only the frame point is checked against expected bytes. A real host checks all.
'use strict';
const base = ptr(HOST.base);
const at = va => base.add(ptr(va).sub(ptr('0x140000000')));
// send() is asynchronous and is lost if the client dies first (a fatal error is shown by a second process while the
// first exits), so anything needed for a post-mortem also goes to a file, flushed at once.
const trace = HOST.overlay ? new File(HOST.overlay + '/../trace.log', 'a') : null;
const log = (what, extra) => {
  const line = Object.assign({ t: what }, extra || {});
  if (trace) { trace.write(JSON.stringify(line) + '\n'); trace.flush(); }
  send(line);
};
const hex = (p, n) => Array.from(new Uint8Array(p.readByteArray(n))).map(x => x.toString(16).padStart(2, '0')).join('');

// name: [va, first bytes]
const SITES = {
  frame:      ['0x141528fbf', '488b5d284883eb10'],   // state-stack update, once per frame on the main thread
  uiLoad:     ['0x1418b08d0', null],                 // void Control::Load(this, const std::wstring& path)
  wAssign:    ['0x140144010', null],                 // std::wstring::assign(this, const wchar_t*, size_t)
  pool:       ['0x1418b6d20', null],                 // control allocator singleton
  newFrame:   ['0x14014b0e0', null],                 // Frame* create(pool)
  addChild:   ['0x1418b5b10', null],                 // void add(children* = control+0xb8, size_t index, Control*)
  findChild:  ['0x1418b6420', null],                 // Control* find(children*, wstring_view* "a.b.c")
  setVisible: ['0x1418b0a30', null],                 // void Control::SetVisible(this, bool)
  eventList:  ['0x14019b2b0', null],                 // shared_ptr<List>* get(event* key, shared_ptr* out, bool create)
  eventGrow:  ['0x140152430', null],                 // void List::grow(this)
};
const A = {};
for (const [name, [va, bytes]] of Object.entries(SITES)) {
  A[name] = at(va);
  if (bytes && hex(A[name], bytes.length / 2) !== bytes) throw new Error('unsupported client: ' + name);
}
// Every client function here uses the Windows x64 convention, which this Linux build of Frida accepts as 'win64'.
const fn = (name, ret, args) => new NativeFunction(A[name], ret, args, { abi: 'win64', traps: 'all' });
const C = {
  uiLoad: fn('uiLoad', 'void', ['pointer', 'pointer']),
  wAssign: fn('wAssign', 'pointer', ['pointer', 'pointer', 'uint64']),
  pool: fn('pool', 'pointer', []),
  newFrame: fn('newFrame', 'pointer', ['pointer']),
  addChild: fn('addChild', 'void', ['pointer', 'uint64', 'pointer']),
  findChild: fn('findChild', 'pointer', ['pointer', 'pointer']),
  setVisible: fn('setVisible', 'void', ['pointer', 'uint8']),
  eventList: fn('eventList', 'pointer', ['pointer', 'pointer', 'uint8']),
  eventGrow: fn('eventGrow', 'void', ['pointer']),
};

// ---- strings ----
const keep = [];
function wstr(text) {            // a real std::wstring, allocated by the client's own allocator
  const s = Memory.alloc(32); s.writeByteArray(new Array(32).fill(0));
  const chars = Memory.allocUtf16String(text);
  C.wAssign(s, chars, text.length);
  keep.push(s, chars);
  return s;
}
function view(text) {            // {const wchar_t*, size_t}
  const v = Memory.alloc(16), chars = Memory.allocUtf16String(text);
  v.writePointer(chars); v.add(8).writeU64(text.length);
  keep.push(v, chars);
  return v;
}
function readW(p) {              // std::wstring at p
  const cap = p.add(24).readU64(), size = p.add(16).readU64().toNumber();
  if (size > 4096) return '?';
  return (cap.compare(8) >= 0 ? p.readPointer() : p).readUtf16String(size);
}

// ---- controls ----
const Control = {
  id: c => readW(c.add(0x1b8)),
  parent: c => c.add(0x1e0).readPointer(),
  flags: c => c.add(0x1e8).readU32(),
  children(c) {
    const out = []; let p = c.add(0xc0).readPointer(); const end = c.add(0xc8).readPointer();
    for (let n = 0; !p.equals(end) && n < 500; p = p.add(8), n++) out.push(p.readPointer());
    return out;
  },
  vtable: c => '0x' + c.readPointer().sub(base).add(ptr('0x140000000')).toString(16),
  root(c) { let n = 0; while (!Control.parent(c).isNull() && n++ < 64) c = Control.parent(c); return c; },
  find: (c, path) => C.findChild(c.add(0xb8), view(path)),
  rect: c => [0x110, 0x114, 0x118, 0x11c].map(o => c.add(o).readFloat()),
  tree(c, depth, max) {
    const lines = [];
    (function walk(node, d) {
      if (lines.length > (max || 200)) return;
      lines.push('  '.repeat(d) + `${node} ${Control.vtable(node)} id=${JSON.stringify(Control.id(node))} flags=${Control.flags(node).toString(16)} kids=${Control.children(node).length}`);
      if (d < depth) for (const k of Control.children(node)) walk(k, d + 1);
    })(c, 0);
    return lines;
  },
  create(parent) {
    const c = C.newFrame(C.pool());
    const kids = parent.add(0xc8).readPointer().sub(parent.add(0xc0).readPointer()).toInt32() >> 3;
    C.addChild(parent.add(0xb8), kids, c);
    return c;
  },
  load: (c, path) => C.uiLoad(c, wstr(path)),
  show: (c, on) => C.setVisible(c, on ? 1 : 0),
  // Label: text interface at +0x298; Button: at +0x3f8. Slot 2 = SetText(const std::wstring&).
  setText(c, text, offset) {
    const sub = c.add(offset === undefined ? 0x298 : offset);
    new NativeFunction(sub.readPointer().add(0x10).readPointer(), 'void', ['pointer', 'pointer'], 'win64')(sub, wstr(text));
  },
};

// ---- events: append {vtable, context, 0} to the handler list of an event (button + 0x2a0 = clicked) ----
const handlers = [];
function onEvent(control, offset, name, run) {
  const invoke = new NativeCallback(function (entry) {
    log('event', { name, control: control.toString() }); events.push(name);
    if (run) try { run(); } catch (e) { log('error', { message: String(e), stack: e.stack }); }
  }, 'void', ['pointer'], 'win64');
  const destroy = new NativeCallback(function (self, flags) { return self; }, 'pointer', ['pointer', 'uint64'], 'win64');
  const size = new NativeCallback(function () { return 0x10; }, 'uint64', [], 'win64');
  const kind = new NativeCallback(function () { return 2; }, 'uint64', [], 'win64');
  const vtable = Memory.alloc(32);
  [destroy, size, kind, invoke].forEach((f, i) => vtable.add(8 * i).writePointer(f));
  const shared = Memory.alloc(16); shared.writeByteArray(new Array(16).fill(0));
  C.eventList(control.add(offset), shared, 1);
  const list = shared.readPointer();
  let start = list.add(8).readPointer(), p = start;
  for (let n = 0; !p.readPointer().isNull() && n < 64; n++) {
    const step = new NativeFunction(p.readPointer().add(8).readPointer(), 'uint64', ['pointer'], 'win64')(p);
    p = p.add(step);
  }
  const capacity = start.equals(list.add(0x10)) ? 0x68 : list.add(0x10).readU64().toNumber();
  if (capacity - p.sub(start).toInt32() < 0x18) {
    C.eventGrow(list);
    const moved = list.add(8).readPointer();
    p = moved.add(p.sub(start)); start = moved;
  }
  p.writePointer(vtable); p.add(8).writePointer(control); p.add(16).writePointer(NULL);
  handlers.push(invoke, destroy, size, kind, vtable, shared);
  return list;
}
const events = [];

// ---- file overlay: text files opened through the client's wide input stream are served from HOST.overlay ----
// bool open(streambuf* this, wstring_view* path, bool keepBom, wstring_view* path): reads the whole file into the
// buffer at this+0x88 and points the get area at it. After it returns, a file found under the overlay folder replaces
// that buffer, whether or not the game had the path. The buffer comes from the client's allocator (16-byte header:
// size, 0, 0, tag<<8 | log2(align)), because the stream frees it.
const served = [];
if (HOST.overlay) {
  const alloc = new NativeFunction(at('0x142c8cf60'), 'pointer', ['uint64', 'uint64'], 'win64');
  Interceptor.attach(at('0x1421d5b50'), {
    onEnter() {
      this.sb = this.context.rcx;
      const v = this.context.rdx;
      this.path = v.readPointer().readUtf16String(v.add(8).readU64().toNumber());
      this.keepBom = this.context.r8.and(0xff).toInt32();
    },
    onLeave(retval) {
      let data;
      try { data = File.readAllBytes(HOST.overlay + '/' + this.path.toLowerCase()); } catch (e) { return; }
      const size = data.byteLength, p = alloc(size + 16, 16).add(16), sb = this.sb, end = p.add(size);
      p.sub(16).writeU32(size); p.sub(12).writeU32(0); p.sub(8).writeU32(0); p.sub(4).writeU32(0x50004);
      p.writeByteArray(data);
      const had = sb.add(0x80).readU8();
      sb.add(0x80).writeU8(1); sb.add(0x88).writePointer(p); sb.add(0x90).writePointer(end); sb.add(0x98).writePointer(end);
      const start = (!this.keepBom && p.readU16() === 0xfeff) ? p.add(2) : p;
      sb.add(0x68).writePointer(start); sb.add(0x70).writePointer(start); sb.add(0x78).writePointer(end);
      sb.add(0x18).readPointer().writePointer(start); sb.add(0x38).readPointer().writePointer(start);
      sb.add(0x50).readPointer().writeS32(end.sub(start).toInt32() / 2);
      retval.replace(ptr(1));
      served.push(this.path);
      log('overlay', { path: this.path, bytes: size, inGame: had });
    },
  });
}

// ---- the file lookup's "not found" throw (0x1421d2ebe inside 0x1421d2bf0): who asked ----
Interceptor.attach(at('0x1421d2ebe'), function () {
  log('missingThrow', { stack: Thread.backtrace(this.context, Backtracer.FUZZY).slice(0, 12).map(a => '0x' + a.sub(base).add(ptr('0x140000000')).toString(16)) });
});

// ---- every C++ throw (_CxxThrowException at 0x142ce6a40): the message, so a parser error is readable ----
// A throw that unwinds through a timer callback is swallowed by user32, so without this a bad .ui file fails silently.
Interceptor.attach(at('0x142ce6a40'), function () {
  const e = this.context.rcx; let narrow = null, wide = null;
  try { narrow = e.add(8).readPointer().readCString(300); } catch (x) {}
  try { wide = readW(e.add(8)); } catch (x) {}
  try { if (!wide || wide === '?') wide = readW(e.add(0x18)); } catch (x) {}
  log('throw', { narrow, wide, at: '0x' + this.returnAddress.sub(base).add(ptr('0x140000000')).toString(16) });
});

// ---- observe every .ui load, so live controls can be found without knowing any global ----
const loads = [];
Interceptor.attach(A.uiLoad, {
  onEnter() {
    const self = this.context.rcx, path = readW(this.context.rdx);
    loads.push({ control: self, path });
    log('uiLoad', { control: self.toString(), path, vtable: Control.vtable(self) });
  },
});

// ---- mod channel over chat (spike S4, no Gateway change) ----
// S→C: the server sends an ordinary chat line (0x17) whose text starts with "##NM "; the chat panel's listener
// 0x14017a270(panel, message) is replaced, so such a line is taken here and never shown. message: sender wstring at
// +0, text wstring at +0x20.
// C→S: a chat line (0x15) written with the client's own buffered writer under the socket lock, exactly as
// LocalExile's scripts/dev/client-control.js does. The server mod receives it as an ordinary chat command.
const queue = [];
const channel = { handlers: {}, received: [] };
const chatListener = at('0x14017a270');
const chatOriginal = new NativeFunction(chatListener, 'void', ['pointer', 'pointer'], 'win64');
Interceptor.replace(chatListener, new NativeCallback(function (panel, message) {
  let text = '';
  try { text = readW(message.add(0x20)); } catch (e) {}
  if (text.startsWith('##NM ')) {
    const rest = text.slice(5), space = rest.indexOf(' ');
    const verb = space < 0 ? rest : rest.slice(0, space), arg = space < 0 ? '' : rest.slice(space + 1);
    log('channelIn', { verb, arg });
    channel.received.push({ verb, arg });
    queue.push(() => { const h = channel.handlers[verb]; if (h) h(arg); else log('error', { message: 'no channel handler for ' + verb }); });
    return;
  }
  chatOriginal(panel, message);
}, 'void', ['pointer', 'pointer'], 'win64'));

function globalAt(va, pattern) {   // a global's address from an instruction that uses it (data moves between builds)
  const p = at(va), gap = pattern.indexOf('?') / 2, actual = hex(p, pattern.length / 2);
  if (![...pattern].every((c, i) => c === '?' || c === actual[i])) throw new Error('unsupported client: global at ' + va);
  return p.add(gap + 4).add(p.add(gap).readS32());
}
const appGlobal = globalAt('0x1400f3b30', '488b05????????488907488b05');
const socketWrite = new NativeFunction(at('0x140dfa000'), 'pointer', ['pointer', 'pointer', 'uint64'], 'win64');
const socketFlush = new NativeFunction(at('0x140dfa160'), 'void', ['pointer', 'uint32', 'uint8'], 'win64');
const acquire = new NativeFunction(at('0x142f4d310').readPointer(), 'void', ['pointer'], 'win64');
const release = new NativeFunction(at('0x142f4d308').readPointer(), 'void', ['pointer'], 'win64');
function sendChat(text) {
  const bytes = [0x00, 0x15, (text.length >> 8) & 0xff, text.length & 0xff];
  for (const ch of text) {
    const cp = ch.codePointAt(0);
    if (cp < 0x80) bytes.push(cp); else bytes.push(0x80, cp & 0xff, (cp >> 8) & 0xff);
  }
  bytes.push(0);
  const instance = appGlobal.readPointer().add(0x348).readPointer();
  if (instance.isNull()) throw new Error('not in an area');
  const socket = instance.add(0xc288).readPointer();
  const p = Memory.alloc(bytes.length); p.writeByteArray(bytes);
  const threaded = socket.add(0x242).readU8() !== 0, lock = socket.add(0x248);
  socket.add(0x240).writeU8(1);
  if (threaded) acquire(lock);
  try { socketWrite(socket, p, bytes.length); } finally { if (threaded) release(lock); socket.add(0x240).writeU8(0); }
  socketFlush(socket, 0, 0);
  log('channelOut', { text });
}

// ---- run inbox scripts on the game thread ----
// Frida does not fire Interceptor hooks for calls made from inside another hook's callback, so work started from the
// frame hook would bypass the file overlay. The frame hook therefore only arms a thread timer (SetTimer with no
// window, through the client's own import slot); the timer callback is called by the game's message pump on the
// same main thread, outside any hook, and drains the queue there.
let frames = 0, timer = null;
function drain() {
  while (queue.length) {
    const source = queue.shift();
    try {
      if (typeof source === 'function') { source(); continue; }
      const result = (new Function('ctx', source))(ctx);
      log('result', { value: result === undefined ? null : result });
    } catch (e) { log('error', { message: String(e), stack: e.stack }); }
  }
}
const timerProc = new NativeCallback(function (hwnd, message, id, time) { drain(); }, 'void', ['pointer', 'uint32', 'uint64', 'uint32'], 'win64');
Interceptor.attach(A.frame, function () {
  frames++;
  if (timer === null) {
    const setTimer = new NativeFunction(at('0x142f4dab8').readPointer(), 'uint64', ['pointer', 'uint64', 'uint32', 'pointer'], 'win64');
    timer = setTimer(NULL, 0, 30, timerProc);
    log('timer', { id: timer.toString(), thread: Process.getCurrentThreadId() });
  }
});
const ctx = { base, at, log, hex, A, C, Control, wstr, view, readW, onEvent, events, loads, served, channel, sendChat, state: {}, get frames() { return frames; } };
function listen() {
  recv('inbox', message => {
    try { queue.push(File.readAllText(message.path)); } catch (e) { log('error', { message: String(e) }); }
    listen();
  });
}
listen();
log('ready', { base: base.toString() });

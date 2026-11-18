// Run through the spike inbox once in game. It is the client half of spikes/server-mod/nmspike.cs: what the real
// client host would do generically for any mod ("open this .ui", "set this property", "tell the server about clicks").
const K = ctx.Control, st = ctx.state;
const hud = ctx.loads.filter(l => /HUD\/HUD\.ui$/i.test(l.path)).pop();
if (!hud) throw new Error("not in game yet (HUD.ui has not been loaded)");
st.gameRoot = K.root(hud.control);
ctx.channel.handlers.open = path => {
  let w = K.find(st.gameRoot, "garden_window");
  if (w.isNull()) {
    K.load(st.gameRoot, path);
    w = K.find(st.gameRoot, "garden_window");
    const name = K.find(w, "body.name_row.seed_name");
    ctx.onEvent(K.find(w, "body.buttons.plant"), 0x2a0, "plant", () => ctx.sendChat("/nm plant " + ctx.readW(name.add(0x338))));
    ctx.onEvent(K.find(w, "body.buttons.harvest"), 0x2a0, "harvest", () => ctx.sendChat("/nm harvest"));
    ctx.onEvent(K.find(w, "body.buttons.close"), 0x2a0, "close", () => K.show(w, false));
  }
  st.garden = w;
  K.show(w, true);
};
ctx.channel.handlers.set = arg => {
  const space = arg.indexOf(" ");
  K.setText(K.find(st.garden, arg.slice(0, space)), arg.slice(space + 1));
};
return "garden demo ready, root " + st.gameRoot;

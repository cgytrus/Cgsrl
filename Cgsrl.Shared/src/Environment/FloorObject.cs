﻿using Cgsrl.Shared.Networking;

using PER.Abstractions.Rendering;
using PER.Util;

namespace Cgsrl.Shared.Environment;

public class FloorObject : SyncedLevelObject {
    protected override RenderCharacter character { get; } = new('.', Color.transparent, new Color(0.1f, 0.1f, 0.1f, 1f));
    public override void Update(TimeSpan time) { }
    public override void Tick(TimeSpan time) { }
}

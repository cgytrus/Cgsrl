﻿using PER.Abstractions.Rendering;
using PER.Util;

namespace Cgsrl.Shared.Environment;

public class BoxObject : MovableObject {
    protected override bool canPush => true;
    protected override float mass => 1f;
    protected override RenderCharacter character { get; } = new('&', Color.transparent, new Color(1f, 1f, 0f, 1f));
}

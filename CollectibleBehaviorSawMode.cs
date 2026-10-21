using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

/// <summary>
/// Даёт пиле ванильный тул-мод «что пилим» (SawModePlan): доски / балки / авто, переключаемые клавишей F. Движок сам
/// рисует меню выбора и синхронизирует режим по сети (как у зубила и кирки-геолога) — своей сетевой логики нет, режим
/// живёт в атрибутах стака под ключом <see cref="ModeAttr"/>. Козлы читают его на СТАРТЕ распила и лочат на бревне
/// (<c>BlockEntitySawhorse.sawMode</c>), поэтому пила — лишь селектор, а не носитель хода.
///
/// Меню показывается ТОЛЬКО когда пила наведена на козлы (<see cref="GetToolModes"/> с блоком в прицеле = BlockEntitySawhorse),
/// зеркаля <c>ItemWrench</c>, который отдаёт режимы блока лишь для блока в прицеле. Так режим читается ровно там, где
/// он нужен, а пила в остальных ситуациях (рубка дерева, любой другой ПКМ) остаётся без тул-мода — F не мусорит меню на
/// каждой пиле мира, что и позволяет безопасно повесить это же поведение на ванильную <c>game:saw</c> патчем (для
/// конфигов AllowVanillaSaw / RemovePitSaw, где обычная пила пилит наравне с колодной).
///
/// <see cref="GetToolMode"/> / <see cref="SetToolMode"/> в блоке в прицеле НЕ нуждаются (читают/пишут атрибут стака) и
/// работают на обеих сторонах: движок зовёт SetToolMode на сервере из пакета тул-мода, но добраться до него можно только
/// через меню, а меню открывается лишь у козел — так что запись фактически достижима только у станка.
/// </summary>
public class CollectibleBehaviorSawMode : CollectibleBehavior
{
    /// <summary>Ключ атрибута стака, где движок-независимо хранится выбранный <see cref="SawMode"/> (0=Auto/1=Planks/2=Beams).
    /// Читается козлами через <see cref="ModeFor"/> на старте распила.</summary>
    public const string ModeAttr = "sawmode";

    SkillItem[]? modes;

    public CollectibleBehaviorSawMode(CollectibleObject collObj) : base(collObj) { }

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        // Иконки и само меню тул-мода — клиентские; сервер GetToolModes не зовёт, так что modes ему не нужны. Три режима
        // собираются один раз и разделяются всеми пилами процессом (идиома кирки-геолога), имя и код — из SawModeExt.
        if (api is not ICoreClientAPI capi) return;
        modes = ObjectCacheUtil.GetOrCreate(capi, "iwSawModeSkillItems", () => new[]
        {
            SkillFor(capi, SawMode.Auto), SkillFor(capi, SawMode.Planks), SkillFor(capi, SawMode.Beams),
        });
    }

    /// <summary>Меню режимов — только у наведённых козел (см. заголовок): вне их возвращаем null, и F ведёт себя как на
    /// пиле без режимов.</summary>
    public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSel)
        => IsSawhorseAt(forPlayer?.Entity?.World, blockSel) ? modes! : null!;

    public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection)
        => (int)ModeFor(slot?.Itemstack);

    public override void SetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection, int toolMode)
    {
        if (slot?.Itemstack == null) return;
        slot.Itemstack.Attributes.SetInt(ModeAttr, GameMath.Clamp(toolMode, 0, 2));
        slot.MarkDirty();
    }

    public override void OnUnloaded(ICoreAPI api)
    {
        // Снимаем иконку с обёртки, НЕ удаляя саму текстуру: GetOrLoadTexture кэширует её на стороне движка, и Dispose
        // здесь испортил бы кэш (плюс массив modes общий на все варианты пилы — было бы двойное освобождение). Обнулив
        // Texture до Dispose, делаем SkillItem.Dispose холостым по текстуре.
        if (modes != null) foreach (SkillItem si in modes) { if (si != null) { si.Texture = null; si.Dispose(); } }
        base.OnUnloaded(api);
    }

    /// <summary>SkillItem для режима: код без домена (идёт в AssetLocation меню), имя из общего <see cref="SawModeExt"/>,
    /// иконка из <c>textures/icons/{code}.png</c> — 48×48 слот меню (движок рисует Texture; без неё кнопка
    /// пустая). Иконка НЕОБЯЗАТЕЛЬНА: грузим только если PNG на месте (<c>Assets.Exists</c> бесплатен, память
    /// vs-client-unloads-textures-after-join), поэтому меню работает и до появления арта, и загорается, как только файл
    /// положат. Текстуру НЕ освобождаем сами (кэш движка) — см. <see cref="OnUnloaded"/>.</summary>
    static SkillItem SkillFor(ICoreClientAPI capi, SawMode m)
    {
        SkillItem si = new SkillItem { Code = new AssetLocation(m.Code()), Name = m.LangName() };
        AssetLocation loc = new AssetLocation("immersivewoodworking", "textures/icons/" + m.Code() + ".png");
        if (capi.Assets.Exists(loc))
        {
            LoadedTexture tex = new LoadedTexture(capi);
            capi.Render.GetOrLoadTexture(loc, ref tex);
            si.WithIcon(capi, tex);
        }
        return si;
    }

    /// <summary>Наведён ли игрок на козлы (мастер или слейв-клетку) — единственное место, где режим пилы имеет смысл.</summary>
    static bool IsSawhorseAt(IWorldAccessor? world, BlockSelection? blockSel)
        => world != null && blockSel != null && world.BlockAccessor.GetBlockEntity(blockSel.Position) is BlockEntitySawhorse;

    /// <summary>Режим, записанный в стаке пилы (Auto по умолчанию / при отсутствии поведения). Статичный хелпер для
    /// козел, читающих ход пилы на старте распила.</summary>
    public static SawMode ModeFor(ItemStack? stack)
    {
        int v = stack?.Attributes?.GetInt(ModeAttr, 0) ?? 0;
        return v is >= 0 and <= 2 ? (SawMode)v : SawMode.Auto;
    }
}

using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace ImmersiveWoodworking;

/// <summary>
/// «Зажми Shift, чтобы поставить» для наших сосудов (BarkPlan/E04, D50). Плейн-ПКМ по блоку с таким предметом в руке
/// показывает ВАНИЛЬНУЮ красную подсказку <c>placefailure-onlywhensneaking</c> («Hold down [Shift] to place this
/// block»), после чего отдаёт клик дальше как ни в чём не бывало. Поведение только ГОВОРИТ — оно не клеймит
/// взаимодействие.
///
/// ЗАЧЕМ. Ванильный обожжённый горшок нагоняет эту подсказку не через <c>GroundStorable</c>, а через свой класс:
/// <c>BlockCookingContainer.TryPlaceBlock</c> (vssurvivalmod/Systems/Cooking/BlockCookingContainer.cs:68-74) на
/// <c>!ShiftKey</c> пишет <c>failureCode = "onlywhensneaking"</c> и выходит ДО <c>CanPlaceBlock</c> — то есть до того,
/// как <c>Unplaceable</c> успеет подставить свою глушилку. Клиент дальше сам собирает ключ:
/// <c>if (failureCode != null &amp;&amp; failureCode != "__ignore__") TriggerIngameError(..., Lang.Get("placefailure-" +
/// failureCode))</c> (VintagestoryLib, <c>SystemMouseInWorldInteractions.HandleMouseInteractionsBlockSelected</c>,
/// декомпиляция 2026-07-29 — единственное место в движке, где этот ключ вообще составляется). Наши сосуды класса с
/// таким перехватом не имели, поэтому доходили до <c>BlockBehaviorUnplaceable.CanPlaceBlock</c>, получали
/// <c>"__ignore__"</c> и молчали: плейн-ПКМ не давал ни звука, ни сообщения, ни действия.
///
/// ПОЧЕМУ CollectibleBehavior, А НЕ override TryPlaceBlock. Запечатанный дегтярный горшок — ПРЕДМЕТ, а движок пускает в
/// путь размещения только стаки с <c>Itemstack.Class == EnumItemClass.Block</c> (та же декомпиляция), так что
/// <c>TryPlaceBlock</c> у него не существует в принципе — override покрыл бы два сосуда из трёх. Одно
/// collectible-поведение работает и на блоке, и на предмете, вешается из JSON ПОВАРИАНТНО через
/// <c>behaviorsByType</c> — а это здесь не украшение, а страховка: <c>tarpot-*-fired</c> открывается ИМЕННО
/// плейн-ПКМ (<see cref="BehaviorTarPotOpen"/>, D40), и подсказка ему противопоказана. Разведение по JSON физически не
/// даёт перепутать состояния, тогда как override в <see cref="ItemTarPot"/> (класс общий для sealed И fired) держался
/// бы на одной строчке гейта.
///
/// ПОЧЕМУ НЕ ВАНИЛЬНЫЙ <c>SneakPlacing</c> (JSON без кода). Он даёт нужный <c>failureCode</c>
/// (vssurvivalmod/BlockBehavior/BehaviorSneakPlacing.cs:26-33), но на наших сосудах мёртв В ЛЮБОМ ПОРЯДКЕ, и порядок
/// тут ни при чём: <c>BlockBehaviorUnplaceable.CanPlaceBlock</c> пишет <c>"__ignore__"</c> БЕЗУСЛОВНО, а цикл
/// <c>Block.CanPlaceBlock</c> (vsapi Block.cs:947-960) не сбрасывает <c>failureCode</c> между поведениями и прерывается
/// только ПОСЛЕ того, как <c>PreventSubsequent</c>-поведение уже записало своё. SneakPlacing раньше — его затирают;
/// позже — он не запускается. Убрать <c>Unplaceable</c> нельзя: горшок станет настоящим ставимым блоком и развалит
/// паритет D47.
///
/// ГЕЙТЫ, и почему каждый нужен:
///   • <c>firstEvent</c> — клиент пере-выстреливает <c>OnHeldInteractStart</c>, пока ПКМ зажат (интервал
///     <c>BuildRepeatDelay</c>), так что без гейта одно нажатие дало бы очередь сообщений. Тот же приём, что у
///     <see cref="BehaviorTarPotOpen"/>; троттлинг по времени (как <c>ingameerror-tarrig-occupied</c>, D46) здесь не
///     нужен — там его требовал путь наземного хранения, который <c>firstEvent</c> не получает вовсе.
///   • <c>!ShiftKey</c> — Shift это и есть правильный жест (<c>GroundStorable</c> кладёт сосуд в клетку), ругаться на
///     него незачем. Ctrl НЕ проверяем: ванильный горшок тоже смотрит только на Shift.
///   • <c>blockSel != null</c> — ванильная подсказка живёт в ветке «выбран блок»; в воздух ванильный горшок молчит.
///   • <c>as ICoreClientAPI</c> — <c>TriggerIngameError</c> клиентский. Сервер сюда и так не попадёт (движок зовёт
///     Start на сервере, только если <c>handHandling != NotHandled</c>, а мы ничего не клеймим), но пусть будет явно.
///
/// НЕ КЛЕЙМИТ. <c>handHandling</c> и <c>handling</c> не трогаются: цикл поведений заводит свежий
/// <c>bhHandling = PassThrough</c> на КАЖДОЕ поведение (vsapi Collectible.cs:1630-1642), поэтому нетронутое значение
/// ничего не меняет — размещение/наземное хранение отрабатывают ровно как раньше, мы лишь добавляем реплику.
///
/// ОДНО ОТЛИЧИЕ ОТ ВАНИЛИ, принятое сознательно. У ванильного горшка перехват стоит в <c>TryPlaceBlock</c> и коротит
/// ДО пред-проверок <c>Block.CanPlaceBlock</c> (<c>notreplaceable</c>/<c>entityintersecting</c>, vsapi Block.cs:923-943).
/// Мы говорим раньше, а движок потом всё равно идёт в путь размещения — поэтому если в целевой клетке стоит существо,
/// наше сообщение будет вытеснено ванильным «A creature is in the way». Случай редкий, и вытесняющий текст точнее
/// нашего, так что оставлено как есть.
/// </summary>
public class BehaviorSneakToPlace : CollectibleBehavior
{
    public BehaviorSneakToPlace(CollectibleObject collObj) : base(collObj) { }

    // Ванильный код провала размещения. Именно из него движок собирает ключ языка "placefailure-onlywhensneaking"
    // ("Hold down <hk>shift</hk> to place this block", game/lang/en.json:5504) — ключ ВАНИЛЬНЫЙ, домен `game`
    // подставляется Lang.Get по умолчанию, поэтому своей строки мод не заводит и все 5 языков получают перевод даром.
    const string FailureCode = "onlywhensneaking";

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (!firstEvent || blockSel == null || byEntity?.Controls == null || byEntity.Controls.ShiftKey) return;

        (byEntity.World?.Api as ICoreClientAPI)?.TriggerIngameError(collObj, FailureCode, Lang.Get("placefailure-" + FailureCode));
        // Намеренно НЕ трогаем handHandling/handling: клик идёт дальше своим ходом.
    }
}

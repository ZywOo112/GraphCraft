using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Graph;
using GraphCraft.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.UI;
using static GraphCraft.UI.CraftGraphUISystem;

namespace GraphCraft.UI;

internal sealed class CraftGraphWindow : UIState
{
	private static readonly Color Panel = new(26, 35, 55, 249);
	private static readonly Color Muted = new(165, 185, 211);
	private static readonly int[] StarterItems = [ItemID.CopperWatch, ItemID.HealingPotion, ItemID.IronPickaxe,
		ItemID.BattlePotion, ItemID.CellPhone];
	private readonly CraftGraphUISystem _system;
	private UIPanel _window = null!;
	private UIPanel _picker = null!;
	private UISearchBar _search = null!;
	private UIList _results = null!;
	private UIScrollbar _resultsScroll = null!;
	private UIList _details = null!;
	private UIScrollbar _detailScroll = null!;
	private UIText _title = null!;
	private UIText _status = null!;
	private UIText _quantity = null!;
	private UIText _footer = null!;
	private CraftGraphCanvas _canvas = null!;
	private GraphButton _back = null!;
	private GraphButton _root = null!;
	private GraphButton _focus = null!;
	private GraphButton _less = null!;
	private GraphButton _more = null!;
	private GraphButton _refresh = null!;
	private CraftGraph? _indexedGraph;
	private GameCulture? _indexedCulture;
	private (int Id, string Name)[] _items = [];
	private bool _pickerOpen;

	public CraftGraphWindow(CraftGraphUISystem system) => _system = system;
	public CraftGraphView? View { get; private set; }
	public bool Typing => _pickerOpen && _search.IsWritingText;

	public override void OnInitialize()
	{
		_window = new UIPanel { BackgroundColor = Panel, BorderColor = new Color(86, 107, 150), HAlign = .5f, VAlign = .5f };
		_window.SetPadding(12);
		_window.Width.Set(-32, 1);
		_window.MaxWidth.Set(1290, 0);
		_window.Height.Set(-32, 1);
		_window.MaxHeight.Set(780, 0);
		Append(_window);

		_title = Header(_window, 3, 1.18f, -177);
		var choose = new GraphButton(Text("SelectTarget"), TogglePicker) { HAlign = 1 };
		choose.Left.Set(-39, 0);
		choose.Width.Set(126, 0);
		_window.Append(choose);
		var close = new GraphButton("×", _system.Close) { HAlign = 1 };
		close.Width.Set(32, 0);
		_window.Append(close);

		_back = ToolbarButton("Back", 0, .115f, () => Navigate(view => view.Back()));
		_root = ToolbarButton("Root", .115f, .115f, () => Navigate(view => view.ReturnToRoot()));
		_focus = ToolbarButton("Focus", .23f, .17f, () => Navigate(view => view.FocusSelection()));
		_refresh = ToolbarButton("Refresh", .40f, .13f, Refresh);
		ToolbarButton("Recenter", .53f, .13f, () => _canvas.Recenter());
		_less = ToolbarButton("Less", .66f, .065f, () => ChangeQuantity(-1));
		_quantity = Header(_window, 42, .83f, -4);
		_quantity.Width.Set(-4, .21f);
		_quantity.Left.Set(0, .725f);
		_quantity.TextOriginX = .5f;
		_more = ToolbarButton("More", .935f, .065f, () => ChangeQuantity(1));
		_status = Header(_window, 73, .89f);
		_status.TextColor = new Color(137, 219, 197);
		_footer = Header(_window, 0, .78f);
		_footer.VAlign = 1;
		_footer.TextColor = Muted;
		_footer.Height.Set(18, 0);

		var canvasClip = new UIElement { OverflowHidden = true };
		canvasClip.Top.Set(100, 0);
		canvasClip.Height.Set(-124, 1);
		canvasClip.Width.Set(-7, .66f);
		_canvas = new CraftGraphCanvas(this);
		canvasClip.Append(_canvas);
		_window.Append(canvasClip);

		var detailPanel = new UIPanel { BackgroundColor = new Color(32, 43, 64), BorderColor = new Color(66, 85, 116) };
		detailPanel.SetPadding(9);
		detailPanel.Top.Set(100, 0);
		detailPanel.Left.Set(0, .66f);
		detailPanel.Width.Set(0, .34f);
		detailPanel.Height.Set(-124, 1);
		_window.Append(detailPanel);
		(_details, _detailScroll) = AppendList(detailPanel, 0);

		_picker = new UIPanel { BackgroundColor = new Color(30, 44, 66), BorderColor = new Color(117, 151, 205), HAlign = .5f };
		_picker.Width.Set(0, .84f);
		_picker.MaxWidth.Set(630, 0);
		_picker.Height.Set(-132, 1);
		_picker.Top.Set(100, 0);
		_search = new UISearchBar(Language.GetText("Mods.GraphCraft.GraphUI.SearchHint"), .95f);
		_search.Width.Set(-44, 1);
		_search.Height.Set(33, 0);
		_search.OnContentsChanged += PopulateResults;
		_picker.Append(_search);
		var closePicker = new GraphButton("×", HidePicker) { HAlign = 1 };
		closePicker.Width.Set(32, 0);
		_picker.Append(closePicker);
		(_results, _resultsScroll) = AppendList(_picker, 46);
		UpdateView(rebuildLayout: true, recenter: true);
	}

	public override void Update(GameTime gameTime)
	{
		base.Update(gameTime);
		if (_window.ContainsPoint(Main.MouseScreen) || Typing) {
			Main.LocalPlayer.mouseInterface = true;
			PlayerInput.LockVanillaMouseScroll("GraphCraft: Craft Graph");
		}
	}

	public override void OnActivate()
	{
		if (View is null)
			ShowPicker(takeInput: false);
	}

	public override void OnDeactivate() => HidePicker();

	public void SetTarget(CraftGraphView view)
	{
		View = view;
		HidePicker();
		UpdateView(rebuildLayout: true, recenter: true);
	}

	public void Refresh()
	{
		if (View is { } view) {
			CraftGraphUISystem.Refresh(view, view.Evidence.Plan.RequestedAdditionalOutputQuantity);
			UpdateView(rebuildLayout: false, recenter: false);
		}
	}

	public void SelectNode(GraphNodeKey key)
	{
		View!.Select(key);
		UpdateView(rebuildLayout: false, recenter: false);
	}

	public void ToggleNode(GraphNodeKey key)
	{
		View!.Select(key);
		UpdateView(rebuildLayout: View.ToggleExpansion(key), recenter: false);
	}

	private void Navigate(Action<CraftGraphView> action)
	{
		if (View is not { } view)
			return;
		action(view);
		UpdateView(rebuildLayout: true, recenter: true);
	}

	private void ChangeQuantity(int change)
	{
		if (View is not { } view)
			return;
		int quantity = Math.Clamp(view.Evidence.Plan.RequestedAdditionalOutputQuantity + change, 1, MaximumQuantity);
		CraftGraphUISystem.Refresh(view, quantity);
		UpdateView(rebuildLayout: false, recenter: false);
	}

	private void UpdateView(bool rebuildLayout, bool recenter)
	{
		if (rebuildLayout) {
			GraphViewLayout? layout = View?.Layout();
			_canvas.SetLayout(layout, recenter);
			string help = layout?.HitDisplayLimit == true ? Text("ViewLimit", layout.Nodes.Count, layout.Links.Count) : Text("CanvasHelp");
			_footer.SetText(help);
		}

		_details.Clear();
		_detailScroll.ViewPosition = 0;
		_back.Enabled = View?.CanGoBack == true;
		_root.Enabled = View is { } rootView && rootView.Focus != rootView.Root;
		_focus.Enabled = View is { } selectionView && selectionView.Selection != selectionView.Focus;
		_refresh.Enabled = View is not null;
		int quantity = View?.Evidence.Plan.RequestedAdditionalOutputQuantity ?? 1;
		_less.Enabled = View is not null && quantity > 1;
		_more.Enabled = View is not null && quantity < MaximumQuantity;
		_quantity.SetText(View is null ? string.Empty : Text("Additional", quantity));
		if (View is not { } view) {
			_title.SetText(Text("Title"));
			_status.SetText(string.Empty);
			return;
		}

		_title.SetText(Text("Heading", Lang.GetItemNameValue(view.Root.LocalId), CraftGraphDetails.NodeName(view.Node(view.Focus))));
		string route = Text("Route." + view.Evidence.Route);
		_status.SetText(view.Subgraph.IsTruncated
			? Text("RouteTruncated", route, view.Subgraph.SkippedFrontierCount)
			: Text("RouteSummary", route));
		_details.AddRange(CraftGraphDetails.Build(view));
	}

	private void TogglePicker()
	{
		if (_pickerOpen)
			HidePicker();
		else
			ShowPicker(takeInput: true);
	}

	private void ShowPicker(bool takeInput)
	{
		if (_pickerOpen)
			return;
		_pickerOpen = true;
		_window.Append(_picker);
		_search.SetContents(string.Empty, forced: true);
		PopulateResults(string.Empty);
		if (takeInput && !_search.IsWritingText)
			_search.ToggleTakingText();
	}

	private void HidePicker()
	{
		if (!_pickerOpen)
			return;
		if (_search.IsWritingText)
			_search.ToggleTakingText();
		_picker.Remove();
		_pickerOpen = false;
	}

	private void PopulateResults(string query)
	{
		if (CraftGraphSystem.Current is not { } graph)
			return;
		if (!ReferenceEquals(graph, _indexedGraph) || Language.ActiveCulture != _indexedCulture) {
			_indexedGraph = graph;
			_indexedCulture = Language.ActiveCulture;
			_items = graph.ItemsByType.Keys.Select(id => (Id: id, Name: Lang.GetItemNameValue(id)))
				.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id).ToArray();
		}

		query = query.Trim();
		IEnumerable<(int Id, string Name)> matches;
		if (query.Length == 0)
			matches = StarterItems.Where(graph.ItemsByType.ContainsKey).Select(id => (id, Lang.GetItemNameValue(id)));
		else if (int.TryParse(query, out int id))
			matches = _items.Where(item => item.Id == id);
		else
			matches = _items.Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));

		var rows = new List<UIElement>();
		foreach ((int itemId, string name) in matches.Take(41)) {
			if (rows.Count == 40) {
				rows.Add(Line(Text("NarrowSearch"), Muted));
				break;
			}
			rows.Add(new GraphButton(Text("SearchResult", name, itemId), () => _system.OpenTarget(itemId, 1), itemId));
		}
		if (rows.Count == 0)
			rows.Add(Line(Text("NoResults"), Muted));
		_results.Clear();
		_resultsScroll.ViewPosition = 0;
		_results.AddRange(rows);
	}

	internal static UIText Line(string text, Color? color = null)
	{
		var line = new UIText(text, .9f) {
			IsWrapped = true, TextOriginX = 0, TextOriginY = 0, WrappedTextBottomPadding = 2, TextColor = color ?? Color.White
		};
		line.Width.Set(0, 1f);
		return line;
	}

	private static UIText Header(UIElement parent, float top, float scale, float offset = 0)
	{
		var label = new UIText(string.Empty, scale) {
			TextOriginX = 0, TextOriginY = 0, DynamicallyScaleDownToWidth = true, IgnoresMouseInteraction = true
		};
		label.Top.Set(top, 0);
		label.Width.Set(offset, 1);
		parent.Append(label);
		return label;
	}

	private GraphButton ToolbarButton(string key, float left, float width, Action action)
	{
		var button = new GraphButton(Text(key), action);
		button.Top.Set(34, 0);
		button.Left.Set(0, left);
		button.Width.Set(-4, width);
		_window.Append(button);
		return button;
	}

	private static (UIList, UIScrollbar) AppendList(UIElement parent, int top)
	{
		var list = new UIList { ListPadding = 5, ManualSortMethod = _ => { } };
		list.Top.Set(top, 0);
		list.Width.Set(-20, 1f);
		list.Height.Set(-top, 1f);
		var scroll = new UIScrollbar { HAlign = 1f };
		scroll.Top.Set(top, 0);
		scroll.Height.Set(-top, 1f);
		parent.Append(list);
		parent.Append(scroll);
		list.SetScrollbar(scroll);
		return (list, scroll);
	}
}

internal sealed class GraphButton : UIElement
{
	private readonly string _label;
	private readonly int _icon;

	public GraphButton(string label, Action action, int icon = 0)
	{
		_label = label;
		_icon = icon;
		Height.Set(icon == 0 ? 29 : 38, 0);
		Width.Set(0, 1);
		OnLeftClick += (_, _) => { if (Enabled) action(); };
	}

	public bool Enabled { get; set; } = true;

	protected override void DrawSelf(SpriteBatch spriteBatch)
	{
		Rectangle bounds = GetDimensions().ToRectangle();
		Color color = Enabled && IsMouseHovering ? new Color(71, 98, 148) : new Color(48, 68, 104);
		CraftGraphCanvas.Fill(spriteBatch, bounds, color);
		int padding = _icon == 0 ? 7 : 36;
		if (_icon != 0)
			CraftGraphCanvas.Icon(spriteBatch, _icon, new Vector2(bounds.X + 18, bounds.Center.Y), 25);
		CraftGraphCanvas.Label(spriteBatch, _label, new Vector2(bounds.X + padding, bounds.Center.Y - 9),
			Enabled ? Color.White : new Color(142, 154, 180), .88f, bounds.Width - padding - 5);
	}
}

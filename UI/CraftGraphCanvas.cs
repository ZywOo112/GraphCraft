using System;
using System.Collections.Generic;
using System.Linq;
using GraphCraft.Core.Analysis;
using GraphCraft.Core.Graph;
using GraphCraft.Core.Resolution;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.UI;
using static GraphCraft.UI.CraftGraphUISystem;

namespace GraphCraft.UI;

internal sealed class CraftGraphCanvas : UIElement
{
	private const int NodeWidth = 190;
	private const int NodeHeight = 52;
	private const int ColumnStride = 250;
	private const int RowStride = 70;
	private static readonly Color CanvasColor = new(16, 22, 36, 245);
	private static readonly Color PlanColor = new(89, 195, 169);
	private static readonly Color GroupColor = new(172, 139, 222);
	private static readonly Color CycleColor = new(226, 151, 94);
	private readonly CraftGraphWindow _window;
	private readonly Dictionary<GraphNodeKey, Point> _positions = new();
	private GraphViewLayout? _layout;
	private Vector2 _pan;
	private Vector2? _dragMouse;
	private float _zoom = 1f;

	public CraftGraphCanvas(CraftGraphWindow window)
	{
		_window = window;
		Width.Set(0, 1f);
		Height.Set(0, 1f);
	}

	public void SetLayout(GraphViewLayout? layout, bool recenter)
	{
		_layout = layout;
		_positions.Clear();
		if (layout is not null) {
			foreach (IGrouping<int, GraphViewNode> column in layout.Nodes.GroupBy(node => node.Column)) {
				int offset = (column.Count() - 1) * RowStride / 2;
				foreach (GraphViewNode node in column)
					_positions[node.Key] = new Point(node.Column * ColumnStride, node.Row * RowStride - offset);
			}
		}
		if (recenter)
			Recenter();
	}

	public void Recenter()
	{
		_pan = Vector2.Zero;
		_zoom = 1f;
		_dragMouse = null;
	}

	public override void LeftMouseDown(UIMouseEvent evt)
	{
		base.LeftMouseDown(evt);
		if (_layout is not null && !TryHit(evt.MousePosition, out _, out _))
			_dragMouse = evt.MousePosition;
	}

	public override void LeftClick(UIMouseEvent evt)
	{
		base.LeftClick(evt);
		if (_dragMouse is null && TryHit(evt.MousePosition, out GraphNodeKey key, out Rectangle bounds)) {
			if (_window.View!.Outgoing(key).Count > 0 && evt.MousePosition.X >= bounds.Right - 26 * _zoom)
				_window.ToggleNode(key);
			else
				_window.SelectNode(key);
		}
	}

	public override void Update(GameTime gameTime)
	{
		base.Update(gameTime);
		if (_dragMouse is not { } previous)
			return;
		if (!Main.mouseLeft || !IsMouseHovering) {
			_dragMouse = null;
			return;
		}
		_pan += Main.MouseScreen - previous;
		_dragMouse = Main.MouseScreen;
	}

	public override void ScrollWheel(UIScrollWheelEvent evt)
	{
		base.ScrollWheel(evt);
		if (_layout is null || evt.ScrollWheelValue == 0)
			return;
		Vector2 canvasOrigin = Origin();
		float nextZoom = Math.Clamp(_zoom * MathF.Pow(1.12f, evt.ScrollWheelValue / 120f), 0.50f, 1.65f);
		_pan += (evt.MousePosition - canvasOrigin) * (1f - nextZoom / _zoom);
		_zoom = nextZoom;
	}

	protected override void DrawSelf(SpriteBatch spriteBatch)
	{
		Rectangle viewport = GetDimensions().ToRectangle();
		Fill(spriteBatch, viewport, CanvasColor);
		if (_layout is null || _window.View is not { } view) {
			Label(spriteBatch, Text("EmptyCanvas"), viewport.Location.ToVector2() + new Vector2(16, 18),
				new Color(157, 173, 195), .95f, viewport.Width - 32);
			return;
		}

		foreach (GraphViewLink link in _layout.Links)
			DrawLink(spriteBatch, view, link);
		foreach (GraphViewNode node in _layout.Nodes) {
			Rectangle bounds = Bounds(node.Key);
			if (viewport.Intersects(bounds))
				DrawNode(spriteBatch, view, node.Key, bounds);
		}
	}

	private void DrawLink(SpriteBatch batch, CraftGraphView view, GraphViewLink link)
	{
		Rectangle source = Bounds(link.Dependency);
		Rectangle target = Bounds(link.Parent);
		bool forward = source.Left > target.Right;
		Vector2 from = new(forward ? source.Left : source.Right, source.Center.Y);
		Vector2 to = new(target.Right, target.Center.Y);
		float elbow = forward ? (from.X + to.X) * .5f : Math.Max(from.X, to.X) + 16 * _zoom;
		if (Math.Abs(from.Y - to.Y) < 1f && !forward) {
			from.Y += source.Height * .27f;
			to.Y -= target.Height * .27f;
		}
		bool group = link.Ingredient?.AcceptsRecipeGroup == true;
		Color color = view.Evidence.IsPlanned(link, view) ? PlanColor :
			link.InCycle ? CycleColor : group ? GroupColor :
			link.Ingredient is null ? new Color(129, 170, 227) : new Color(104, 125, 157);
		Vector2 corner = new(elbow, from.Y);
		Vector2 entry = new(elbow, to.Y);
		Line(batch, from, corner, color);
		Line(batch, corner, entry, color);
		Line(batch, entry, to, color);
		Line(batch, to, to + new Vector2(6, -4) * _zoom, color);
		Line(batch, to, to + new Vector2(6, 4) * _zoom, color);
		if (group && _zoom >= .68f)
			Label(batch, Text("EdgeOr"), Vector2.Lerp(from, corner, .45f) + new Vector2(-5, -14),
				color, .69f * _zoom, Math.Abs(from.X - corner.X) + 10);
	}

	private void DrawNode(SpriteBatch batch, CraftGraphView view, GraphNodeKey key, Rectangle bounds)
	{
		Vector2 upper = bounds.Location.ToVector2();
		float textScale = .86f * _zoom;
		bool isSelected = key == view.Selection;
		bool isFocus = key == view.Focus;
		ICraftGraphNode node = view.Node(key);
		Color accent;
		string title;
		string status;
		bool expandable = view.Outgoing(key).Count > 0;
		int iconType;
		if (node is ItemNode item) {
			ItemResolution evidence = view.Evidence.Resolution.GetItemResolution(item);
			accent = new Color(126, 174, 232);
			title = Lang.GetItemNameValue(item.ItemType);
			status = Text("StockShort", evidence.CurrentCount);
			iconType = item.ItemType;
		}
		else {
			var recipe = (RecipeNode)node;
			RecipeLocalState local = view.Evidence.Resolution.GetRecipeResolution(recipe).State;
			accent = local == RecipeLocalState.ReadyNow ? PlanColor : new Color(225, 173, 113);
			title = Text("RecipeCard", recipe.RecipeIndex, recipe.OutputStack);
			status = Text("Local." + local);
			iconType = recipe.Result.ItemType;
		}
		if (view.IsCyclic(key))
			status += " · " + Text("CycleShort");
		else if (view.IsShared(key))
			status += " · " + Text("Shared");

		Fill(batch, bounds, isSelected ? new Color(182, 215, 255) : isFocus ? accent : new Color(56, 73, 104));
		var interior = new Rectangle(bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4);
		Fill(batch, interior,
			node is ItemNode ? new Color(35, 51, 78) : new Color(48, 44, 67));
		Fill(batch, new Rectangle(bounds.X + 2, bounds.Y + 2, 3, bounds.Height - 4), accent);
		Icon(batch, iconType, upper + new Vector2(23, 26) * _zoom, 29 * _zoom);
		float rightMargin = expandable ? 27 : 6;
		float textWidth = (NodeWidth - 43 - rightMargin) * _zoom;
		Label(batch, title, upper + new Vector2(43, 7) * _zoom, Color.White, textScale, textWidth);
		Label(batch, status, upper + new Vector2(43, 27) * _zoom, accent, .76f * _zoom, textWidth);
		if (expandable) {
			var button = new Rectangle(bounds.Right - (int)(25 * _zoom), bounds.Top + 2,
				(int)(23 * _zoom), bounds.Height - 4);
			Fill(batch, button, new Color(59, 76, 106));
			Label(batch, view.IsExpanded(key) ? "−" : "+", button.Location.ToVector2() + new Vector2(5, 15) * _zoom,
				Color.White, .95f * _zoom, button.Width);
		}
	}

	private bool TryHit(Vector2 mouse, out GraphNodeKey key, out Rectangle bounds)
	{
		if (_layout is not null && GetDimensions().ToRectangle().Contains(mouse.ToPoint())) {
			foreach (GraphViewNode node in _layout.Nodes) {
				bounds = Bounds(node.Key);
				if (bounds.Contains(mouse.ToPoint())) {
					key = node.Key;
					return true;
				}
			}
		}
		bounds = default;
		key = default;
		return false;
	}

	private Vector2 Origin()
	{
		CalculatedStyle style = GetDimensions();
		return new Vector2(style.X + 20, style.Y + style.Height * .42f) + _pan;
	}

	private Rectangle Bounds(GraphNodeKey key)
	{
		Vector2 point = Origin() + (_positions[key].ToVector2() - new Vector2(0, NodeHeight / 2f)) * _zoom;
		return new Rectangle((int)point.X, (int)point.Y, (int)(NodeWidth * _zoom), (int)(NodeHeight * _zoom));
	}

	internal static void Fill(SpriteBatch batch, Rectangle bounds, Color color) =>
		batch.Draw(TextureAssets.MagicPixel.Value, bounds, color);

	internal static void Icon(SpriteBatch batch, int itemType, Vector2 center, float size)
	{
		Main.instance.LoadItem(itemType);
		Texture2D texture = TextureAssets.Item[itemType].Value;
		Rectangle frame = Main.itemAnimations[itemType]?.GetFrame(texture) ?? texture.Bounds;
		float scale = Math.Min(1.25f, size / Math.Max(frame.Width, frame.Height));
		batch.Draw(texture, center, frame, Color.White, 0, new Vector2(frame.Width, frame.Height) / 2f, scale, SpriteEffects.None, 0);
	}

	internal static void Label(SpriteBatch batch, string text, Vector2 position, Color color, float scale, float width)
	{
		if (width <= 0)
			return;
		float measured = FontAssets.MouseText.Value.MeasureString(text).X;
		if (measured * scale > width)
			scale = Math.Max(scale * .74f, width / measured);
		if (measured * scale > width) {
			int length = text.Length;
			while (length > 0 && FontAssets.MouseText.Value.MeasureString(text[..length] + "…").X * scale > width)
				length--;
			text = text[..length] + "…";
		}
		Utils.DrawBorderString(batch, text, position, color, scale);
	}

	private static void Line(SpriteBatch batch, Vector2 start, Vector2 end, Color color)
	{
		Vector2 edge = end - start;
		if (edge.LengthSquared() > .01f)
			batch.Draw(TextureAssets.MagicPixel.Value, start, null, color, MathF.Atan2(edge.Y, edge.X), Vector2.Zero,
				new Vector2(edge.Length(), 2), SpriteEffects.None, 0);
	}
}

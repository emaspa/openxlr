// A selector DOM that behaves as a browser's single select does: setting a
// value no option holds leaves the first enabled option selected, and so does
// removing the selected option. That is what made the page read empty.
export class Element {
  constructor(tag) {
    this.tag = tag; this.children = []; this.dataset = {}; this.listeners = new Map();
    this.disabled = false; this.hidden = false; this._value = ""; this.selected = null;
  }
  appendChild(child) { child.parent = this; this.children.push(child); this.root().settle(); }
  append(...children) { for (const child of children) if (typeof child === "string") this.children.push(child); else this.appendChild(child); }
  insertBefore(child, ref) {
    child.parent = this; this.children.splice(this.children.indexOf(ref), 0, child); this.root().settle();
  }
  remove() {
    const root = this.root();
    this.parent.children = this.parent.children.filter(child => child !== this);
    this.parent = null;
    root.settle();
  }
  set innerHTML(_) { this.children = []; }
  root() { return this.parent ? this.parent.root() : this; }
  querySelector(selector) {
    return selector === "[data-layout-anchor]" ? this.children.find(child => "layoutAnchor" in child.dataset) ?? null : null;
  }
  querySelectorAll() { return this.children.filter(child => child.dataset.layoutGroup); }
  get options() { return this.children.flatMap(child => child.tag === "option" ? [child] : child.options ?? []); }
  get selectedOptions() { return this.selected ? [this.selected] : []; }
  get value() {
    if (this.tag === "select") return this.selected?.value ?? "";
    return this._value;
  }
  set value(value) {
    if (this.tag !== "select") { this._value = value; return; }
    this.selected = this.options.find(option => option.value === value) ?? null;
    this.settle();
  }
  settle() {
    if (this.tag !== "select") return;
    if (!this.options.includes(this.selected)) this.selected = this.options.find(option => !option.disabled) ?? null;
  }
  addEventListener(event, callback) { this.listeners.set(event, callback); }
  // A choice made by the user in the list.
  choose(value) { this.value = value; this.listeners.get("change")(); }
}

export const option = (value, text = value) => {
  const element = new Element("option");
  element.value = value; element.textContent = text;
  return element;
};

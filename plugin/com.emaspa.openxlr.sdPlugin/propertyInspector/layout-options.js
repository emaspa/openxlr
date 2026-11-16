// Replace only mixer-layout groups. Hardware, output/feed, insert and profile
// groups belong to their existing code paths and remain in the selector. The
// generated groups go where the page's anchor sits (right after the static
// groups, ahead of the feed, insert and profile groups), not at the end.
function replaceLayoutOptions(groups, wanted) {
  const select = document.getElementById("target");
  const selected = wanted || select.value || "";
  const anchor = layoutAnchor(select);
  const place = (node) => anchor ? select.insertBefore(node, anchor) : select.appendChild(node);
  for (const group of select.querySelectorAll("[data-layout-group]")) group.remove();
  document.getElementById("unavailable-layout-target")?.remove();
  for (const group of groups) {
    const element = document.createElement("optgroup");
    element.dataset.layoutGroup = "true";
    element.label = group.label;
    for (const item of group.items) {
      const option = document.createElement("option");
      option.value = item.target;
      option.textContent = item.label;
      element.appendChild(option);
    }
    place(element);
  }
  selectTarget(select, selected);
}

function layoutAnchor(select) {
  return select.querySelector ? select.querySelector("[data-layout-anchor]") : null;
}

// Select a saved target whatever its kind. A target with no listed option
// (its channel, output, insert or profile is gone, or the daemon is offline
// and nothing was listed yet) stays selected as a disabled "Unavailable"
// entry, so the page shows what the key holds and a save keeps it. The entry
// goes once a real option for the target is listed.
function selectTarget(select, wanted) {
  document.getElementById("unavailable-layout-target")?.remove();
  if (wanted && !Array.from(select.options).some(option => option.value === wanted)) {
    const unavailable = document.createElement("option");
    unavailable.id = "unavailable-layout-target";
    unavailable.value = wanted;
    unavailable.textContent = "Unavailable: " + wanted;
    unavailable.disabled = true;
    const anchor = layoutAnchor(select);
    if (anchor) select.insertBefore(unavailable, anchor);
    else select.appendChild(unavailable);
  }
  select.value = wanted;
}

// Replace only mixer-layout groups. Hardware, output/feed, insert and profile
// groups belong to their existing code paths and remain in the selector.
function replaceLayoutOptions(groups, wanted) {
  const select = document.getElementById("target");
  const selected = select.value || wanted || "";
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
    select.appendChild(element);
  }
  if (/^(mixvol:|mixmute:|send:|sendmute:)/.test(selected) &&
      !Array.from(select.options).some(option => option.value === selected)) {
    const unavailable = document.createElement("option");
    unavailable.id = "unavailable-layout-target";
    unavailable.value = selected;
    unavailable.textContent = "Unavailable: " + selected;
    unavailable.disabled = true;
    select.appendChild(unavailable);
  }
  select.value = selected;
}

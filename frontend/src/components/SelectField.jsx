import { useEffect, useId, useMemo, useRef, useState } from "react";

function findNextEnabledIndex(options, startIndex, direction) {
  if (options.length === 0) {
    return -1;
  }

  let index = startIndex;
  for (let step = 0; step < options.length; step += 1) {
    index = (index + direction + options.length) % options.length;
    if (!options[index]?.disabled) {
      return index;
    }
  }

  return -1;
}

export function SelectField({
  ariaLabel,
  className = "",
  disabled = false,
  menuClassName = "",
  name,
  onChange,
  options,
  triggerClassName = "",
  value
}) {
  const listboxId = useId();
  const rootRef = useRef(null);
  const buttonRef = useRef(null);
  const optionRefs = useRef([]);
  const [isOpen, setIsOpen] = useState(false);
  const [opensUpward, setOpensUpward] = useState(false);
  const normalizedValue = value ?? "";

  const selectedIndex = useMemo(
    () => options.findIndex((option) => option.value === normalizedValue),
    [normalizedValue, options]
  );
  const firstEnabledIndex = useMemo(
    () => options.findIndex((option) => !option.disabled),
    [options]
  );
  const [activeIndex, setActiveIndex] = useState(selectedIndex >= 0 ? selectedIndex : firstEnabledIndex);

  const selectedOption = selectedIndex >= 0 ? options[selectedIndex] : null;
  const displayLabel = selectedOption?.label ?? "";
  const isPlaceholder = normalizedValue === "";

  useEffect(() => {
    if (!isOpen) {
      setActiveIndex(selectedIndex >= 0 ? selectedIndex : firstEnabledIndex);
    }
  }, [firstEnabledIndex, isOpen, selectedIndex]);

  useEffect(() => {
    if (!isOpen) {
      return undefined;
    }

    function handlePointerDown(event) {
      if (rootRef.current && !rootRef.current.contains(event.target)) {
        setIsOpen(false);
      }
    }

    function handleKeyDown(event) {
      if (event.key === "Escape") {
        setIsOpen(false);
        buttonRef.current?.focus();
      }
    }

    document.addEventListener("mousedown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);

    return () => {
      document.removeEventListener("mousedown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const rect = buttonRef.current?.getBoundingClientRect();
    if (rect) {
      const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
      const estimatedMenuHeight = Math.min(Math.max(options.length * 48 + 16, 140), 320);
      setOpensUpward(viewportHeight - rect.bottom < estimatedMenuHeight && rect.top > estimatedMenuHeight);
    }

    window.requestAnimationFrame(() => {
      const target = optionRefs.current[activeIndex] ?? optionRefs.current[selectedIndex] ?? optionRefs.current[firstEnabledIndex];
      target?.focus();
    });
  }, [activeIndex, firstEnabledIndex, isOpen, options.length, selectedIndex]);

  function openMenu(preferredIndex) {
    if (disabled) {
      return;
    }

    setActiveIndex(preferredIndex);
    setIsOpen(true);
  }

  function commitValue(index) {
    const option = options[index];
    if (!option || option.disabled) {
      return;
    }

    onChange(option.value);
    setIsOpen(false);
    buttonRef.current?.focus();
  }

  function moveActiveIndex(direction) {
    const nextIndex = findNextEnabledIndex(
      options,
      activeIndex >= 0 ? activeIndex : selectedIndex >= 0 ? selectedIndex : firstEnabledIndex,
      direction
    );

    if (nextIndex >= 0) {
      setActiveIndex(nextIndex);
    }
  }

  function handleButtonKeyDown(event) {
    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        if (isOpen) {
          moveActiveIndex(1);
        } else {
          openMenu(selectedIndex >= 0 ? selectedIndex : firstEnabledIndex);
        }
        break;
      case "ArrowUp":
        event.preventDefault();
        if (isOpen) {
          moveActiveIndex(-1);
        } else {
          openMenu(selectedIndex >= 0 ? selectedIndex : firstEnabledIndex);
        }
        break;
      case "Enter":
      case " ":
        event.preventDefault();
        setIsOpen((current) => !current);
        break;
      default:
        break;
    }
  }

  function handleOptionKeyDown(event, index) {
    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        moveActiveIndex(1);
        break;
      case "ArrowUp":
        event.preventDefault();
        moveActiveIndex(-1);
        break;
      case "Home":
        event.preventDefault();
        setActiveIndex(firstEnabledIndex);
        break;
      case "End":
        event.preventDefault();
        setActiveIndex(findNextEnabledIndex(options, firstEnabledIndex, -1));
        break;
      case "Enter":
      case " ":
        event.preventDefault();
        commitValue(index);
        break;
      case "Tab":
        setIsOpen(false);
        break;
      default:
        break;
    }
  }

  return (
    <div
      ref={rootRef}
      className={`select-shell ${isOpen ? "is-open" : ""} ${opensUpward ? "opens-upward" : ""} ${className}`.trim()}
    >
      {name ? <input type="hidden" name={name} value={normalizedValue} /> : null}
      <button
        ref={buttonRef}
        type="button"
        className={`select-trigger ${isPlaceholder ? "is-placeholder" : ""} ${triggerClassName}`.trim()}
        aria-haspopup="listbox"
        aria-expanded={isOpen}
        aria-controls={isOpen ? listboxId : undefined}
        aria-label={ariaLabel}
        disabled={disabled}
        onClick={() => {
          if (disabled) {
            return;
          }

          setIsOpen((current) => !current);
        }}
        onKeyDown={handleButtonKeyDown}
      >
        <span className="select-trigger-label">{displayLabel}</span>
        <svg className="select-trigger-icon" viewBox="0 0 16 16" aria-hidden="true">
          <path d="M4 6.5 8 10.5 12 6.5" />
        </svg>
      </button>

      {isOpen && (
        <div id={listboxId} role="listbox" className={`select-menu ${menuClassName}`.trim()}>
          {options.map((option, index) => {
            const isSelected = option.value === normalizedValue;
            const isActive = index === activeIndex;

            return (
              <button
                key={`${String(option.value)}-${index}`}
                ref={(element) => {
                  optionRefs.current[index] = element;
                }}
                type="button"
                role="option"
                aria-selected={isSelected}
                className={`select-option ${isSelected ? "is-selected" : ""} ${isActive ? "is-active" : ""}`.trim()}
                disabled={option.disabled}
                onClick={() => commitValue(index)}
                onKeyDown={(event) => handleOptionKeyDown(event, index)}
                onMouseEnter={() => setActiveIndex(index)}
              >
                <span>{option.label}</span>
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

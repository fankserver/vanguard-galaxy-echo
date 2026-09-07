TFM      := netstandard2.1
CONFIG   := Debug
DLL      := VGEcho.dll

BUILDDIR := VGEcho/bin/$(CONFIG)/$(TFM)
BUILDDLL := $(BUILDDIR)/$(DLL)

# WSL path to the game install — adjust if Steam lives elsewhere
GAME_DIR := /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
PLUGIN_DIR := $(GAME_DIR)/BepInEx/plugins/VGEcho

# VGModAPI's consumer contract, needed to compile the autopilot arrival-snap
# bridge. COMPILE-ONLY: the API plugin ships and loads this assembly itself, so
# it is never committed here and never copied into the VGEcho package.
#
# The default is the sibling API checkout, the layout every plugin in this
# workspace uses. Override it when the API lives elsewhere:
#   make build VGAPI_DLL=/path/to/VGModAPI.Abstractions.dll
# Build it in the API checkout with `make build CONFIGURATION=Release` first.
VGAPI_DLL ?= ../vanguard-galaxy-api/VGModAPI.Abstractions/bin/Release/netstandard2.1/VGModAPI.Abstractions.dll

# Resolve dotnet — prefer explicit local SDK, fall back to PATH
DOTNET   ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)

FEATURE_TESTS := VGEcho.Tests/VGEcho.Tests.csproj
COMPAT_TESTS  := VGEcho.Compatibility.Tests/VGEcho.Compatibility.Tests.csproj

# Both suites target net8.0, but dev boxes and CI often carry only a newer
# runtime. LatestMajor lets the host roll forward instead of pinning an SDK.
export DOTNET_ROLL_FORWARD := LatestMajor

# A --filter that matches nothing exits 0 by default, which would turn a renamed
# or dropped Category into a silent green run. Fail instead.
NO_TESTS_IS_FAILURE := -- RunConfiguration.TreatNoTestsAsError=true

.PHONY: all build test check-bindings compat-test compat-check-bindings \
        link-asm link-api link-libs clean deploy check-bepinex

all: build

check-bepinex:
	@test -d "$(GAME_DIR)/BepInEx/plugins" || { \
		echo "BepInEx plugins dir not found at $(GAME_DIR)/BepInEx/plugins." ; \
		echo "Install BepInEx 5.x into the game folder and launch the game once." ; \
		exit 1 ; \
	}

# Symlink the game's Assembly-CSharp.dll into VGEcho/lib/ for compilation references.
link-asm:
	@mkdir -p VGEcho/lib
	@if [ ! -e "VGEcho/lib/Assembly-CSharp.dll" ]; then \
		ln -sf "$(GAME_DIR)/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll" VGEcho/lib/Assembly-CSharp.dll ; \
		echo "Linked Assembly-CSharp.dll" ; \
	fi

# Relinks only when there is no resolvable link yet, so a one-off
# `make build VGAPI_DLL=...` keeps working for later targets. Delete
# VGEcho/lib/VGModAPI.Abstractions.dll to repoint it.
link-api:
	@mkdir -p VGEcho/lib
	@if [ ! -s "VGEcho/lib/VGModAPI.Abstractions.dll" ]; then \
		test -s "$(VGAPI_DLL)" || { \
			echo "VGModAPI.Abstractions.dll not found at $(VGAPI_DLL)." ; \
			echo "Build the API Release output in a sibling checkout, or set VGAPI_DLL to its path." ; \
			exit 1 ; \
		} ; \
		ln -sfn "$(abspath $(VGAPI_DLL))" VGEcho/lib/VGModAPI.Abstractions.dll ; \
		echo "Linked VGModAPI.Abstractions.dll" ; \
	fi

link-libs: link-asm link-api

build: link-libs
	DOTNET_ROOT=$(dir $(DOTNET)) $(DOTNET) build VGEcho/VGEcho.csproj -c $(CONFIG)

# Asset-free suite: pure reducer/binding decisions plus Cecil metadata checks
# against the freshly built plugin. Needs no game install and no BepInEx.
test: build
	VGECHO_ASSEMBLY="$(abspath $(BUILDDLL))" DOTNET_ROOT=$(dir $(DOTNET)) \
		$(DOTNET) test $(FEATURE_TESTS) -c $(CONFIG) --filter 'Category!=InstalledGame' $(NO_TESTS_IS_FAILURE)

# Compatibility checks against the ORIGINAL installed game assembly, read as
# metadata only. Requires a local install; not runnable in public CI.
check-bindings: build
	VGECHO_ASSEMBLY="$(abspath $(BUILDDLL))" \
	VG_GAME_ASSEMBLY="$(GAME_DIR)/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll" \
	DOTNET_ROOT=$(dir $(DOTNET)) \
		$(DOTNET) test $(FEATURE_TESTS) -c $(CONFIG) --filter 'Category=InstalledGame' $(NO_TESTS_IS_FAILURE)

# Game-compatibility regression suite: the linked native-Remove binding helper
# exercised against synthetic inventories, plus Cecil metadata/IL checks over
# the freshly built plugin. Needs no game install and no BepInEx.
compat-test: build
	VGECHO_ASSEMBLY="$(abspath $(BUILDDLL))" DOTNET_ROOT=$(dir $(DOTNET)) \
		$(DOTNET) test $(COMPAT_TESTS) -c $(CONFIG) --filter 'Category!=InstalledGame' $(NO_TESTS_IS_FAILURE)

# Signature checks against the ORIGINAL installed game assembly, read as
# metadata only. Requires a local install; not runnable in public CI.
compat-check-bindings: build
	VGECHO_ASSEMBLY="$(abspath $(BUILDDLL))" \
	VG_GAME_ASSEMBLY="$(GAME_DIR)/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll" \
	DOTNET_ROOT=$(dir $(DOTNET)) \
		$(DOTNET) test $(COMPAT_TESTS) -c $(CONFIG) --filter 'Category=InstalledGame' $(NO_TESTS_IS_FAILURE)

deploy: build check-bepinex
	@mkdir -p "$(PLUGIN_DIR)"
	cp "$(BUILDDLL)" "$(PLUGIN_DIR)/"
	@if [ -f "$(BUILDDIR)/VGEcho.pdb" ]; then cp "$(BUILDDIR)/VGEcho.pdb" "$(PLUGIN_DIR)/"; fi
	@echo "Deployed $(DLL) to $(PLUGIN_DIR)"

clean:
	$(DOTNET) clean VGEcho/VGEcho.csproj
	rm -rf VGEcho/bin VGEcho/obj VGEcho.Tests/bin VGEcho.Tests/obj \
		VGEcho.Compatibility.Tests/bin VGEcho.Compatibility.Tests/obj

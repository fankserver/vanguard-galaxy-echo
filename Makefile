TFM      := netstandard2.1
CONFIG   := Debug
DLL      := VGEcho.dll

BUILDDIR := VGEcho/bin/$(CONFIG)/$(TFM)
BUILDDLL := $(BUILDDIR)/$(DLL)

# WSL path to the game install — adjust if Steam lives elsewhere
GAME_DIR := /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
PLUGIN_DIR := $(GAME_DIR)/BepInEx/plugins/VGEcho

# Resolve dotnet — prefer explicit local SDK, fall back to PATH
DOTNET   ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)

COMPAT_TESTS := VGEcho.Compatibility.Tests/VGEcho.Compatibility.Tests.csproj

# VGEcho.Compatibility.Tests targets net8.0, but dev boxes and CI often carry
# only a newer runtime. LatestMajor lets the host roll forward instead of
# pinning an SDK.
export DOTNET_ROLL_FORWARD := LatestMajor

.PHONY: all build compat-test compat-check-bindings link-asm clean deploy check-bepinex

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

build: link-asm
	DOTNET_ROOT=$(dir $(DOTNET)) $(DOTNET) build VGEcho/VGEcho.csproj -c $(CONFIG)

# Game-compatibility regression suite: the linked native-Remove binding helper
# exercised against synthetic inventories, plus Cecil metadata/IL checks over
# the freshly built plugin. Needs no game install and no BepInEx.
compat-test: build
	VGECHO_ASSEMBLY="$(abspath $(BUILDDLL))" DOTNET_ROOT=$(dir $(DOTNET)) \
		$(DOTNET) test $(COMPAT_TESTS) -c $(CONFIG) --filter 'Category!=InstalledGame'

# Signature checks against the ORIGINAL installed game assembly, read as
# metadata only. Requires a local install; not runnable in public CI.
compat-check-bindings: build
	VGECHO_ASSEMBLY="$(abspath $(BUILDDLL))" \
	VG_GAME_ASSEMBLY="$(GAME_DIR)/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll" \
	DOTNET_ROOT=$(dir $(DOTNET)) \
		$(DOTNET) test $(COMPAT_TESTS) -c $(CONFIG) --filter 'Category=InstalledGame'

deploy: build check-bepinex
	@mkdir -p "$(PLUGIN_DIR)"
	cp "$(BUILDDLL)" "$(PLUGIN_DIR)/"
	@if [ -f "$(BUILDDIR)/VGEcho.pdb" ]; then cp "$(BUILDDIR)/VGEcho.pdb" "$(PLUGIN_DIR)/"; fi
	@echo "Deployed $(DLL) to $(PLUGIN_DIR)"

clean:
	$(DOTNET) clean VGEcho/VGEcho.csproj
	rm -rf VGEcho/bin VGEcho/obj VGEcho.Compatibility.Tests/bin VGEcho.Compatibility.Tests/obj

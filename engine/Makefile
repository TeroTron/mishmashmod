############################# INSTRUCTIONS #############################
#
# to compile, run:
#   make
#
# to compile using system libraries for native dependencies, run:
#   make TARGETPLATFORM=unix-generic
#
# to check the official mods for erroneous yaml files, run:
#   make test
#
# to check the engine and official mod dlls for code style violations, run:
#   make check
#
# to compile and install Red Alert, Tiberian Dawn, and Dune 2000, run:
#   make [prefix=/foo] [bindir=/bar/bin] install
#
# to compile and install Red Alert, Tiberian Dawn, and Dune 2000
# using system libraries for native dependencies, run:
#   make [prefix=/foo] [bindir=/bar/bin] TARGETPLATFORM=unix-generic install
#
# to install FreeDesktop startup scripts, desktop files, icons, and MIME metadata
#   make install-linux-shortcuts
#
# to install FreeDesktop AppStream metadata
#   make install-linux-appdata
#
# to install the Unix man page
#   make install-man
#
# for help, run:
#   make help
#

######################### UTILITIES/SETTINGS ###########################
#
# Install locations for local installs and downstream packaging
prefix ?= /usr/local
datarootdir ?= $(prefix)/share
datadir ?= $(datarootdir)
mandir ?= $(datarootdir)/man/
bindir ?= $(prefix)/bin
libdir ?= $(prefix)/lib
gameinstalldir ?= $(libdir)/openra

# Toolchain
CWD = $(shell pwd)
MSBUILD = msbuild -verbosity:m -nologo
DOTNET = dotnet
RM = rm
RM_R = $(RM) -r
RM_F = $(RM) -f
RM_RF = $(RM) -rf

CONFIGURATION ?= Release
DOTNET_RID = $(shell ${DOTNET} --info | grep RID: | cut -w -f3)
ARCH_X64 = $(shell echo ${DOTNET_RID} | grep x64)

# Only for use in target version:
VERSION := $(shell git name-rev --name-only --tags --no-undefined HEAD 2>/dev/null || (c=$$(git rev-parse --short HEAD 2>/dev/null) && echo git-$$c))

# Detect target platform for dependencies if not given by the user
ifndef TARGETPLATFORM
UNAME_S := $(shell uname -s)
UNAME_M := $(shell uname -m)
ifeq ($(UNAME_S),Darwin)
ifeq ($(ARCH_X64),)
TARGETPLATFORM = osx-arm64
else
TARGETPLATFORM = osx-x64
endif
else
ifeq ($(UNAME_M),x86_64)
TARGETPLATFORM = linux-x64
else
ifeq ($(UNAME_M),aarch64)
TARGETPLATFORM = linux-arm64
else
TARGETPLATFORM = unix-generic
endif
endif
endif
endif

##################### DEVELOPMENT BUILDS AND TESTS #####################
#
all:
	@echo "Compiling in ${CONFIGURATION} mode..."
	@$(DOTNET) build -c ${CONFIGURATION} -nologo -p:TargetPlatform=$(TARGETPLATFORM)
ifeq ($(TARGETPLATFORM), unix-generic)
	@./configure-system-libraries.sh
endif
	@./fetch-geoip.sh

# Deleting the intermediate / output directories ensures the build directory is actually clean
clean:
	@-$(RM_RF) ./bin ./*/obj
	@-$(RM_F) IP2LOCATION-LITE-DB1.IPV6.BIN.ZIP

check:
	@echo
	@echo "Compiling in Debug mode..."
	@$(DOTNET) clean -c Debug --nologo --verbosity minimal
	@$(DOTNET) build -c Debug -nologo -warnaserror -p:TargetPlatform=$(TARGETPLATFORM)
ifeq ($(TARGETPLATFORM), unix-generic)
	@./configure-system-libraries.sh
endif
	@echo
	@echo "Checking for explicit interface violations..."
	@./utility.sh all --check-explicit-interfaces
	@echo
	@echo "Checking for incorrect conditional trait interface overrides..."
	@./utility.sh all --check-conditional-trait-interface-overrides

check-scripts:
	@echo
	@echo "Checking for Lua syntax errors..."
	@find mods/*/maps/ mods/*/scripts/ -iname "*.lua" -print0 | xargs -0n1 luac -p

test: all
	@echo
	@echo "Testing Tiberian Sun mod MiniYAML..."
	@./utility.sh ts --check-yaml
	@echo
	@echo "Testing Dune 2000 mod MiniYAML..."
	@./utility.sh d2k --check-yaml
	@echo
	@echo "Testing Tiberian Dawn mod MiniYAML..."
	@./utility.sh cnc --check-yaml
	@echo
	@echo "Testing Red Alert mod MiniYAML..."
	@./utility.sh ra --check-yaml

tests:
	@dotnet build OpenRA.Test/OpenRA.Test.csproj -c Debug --nologo -p:TargetPlatform=$(TARGETPLATFORM)
	@echo
	@dotnet test bin/OpenRA.Test.dll --test-adapter-path:.

############# LOCAL INSTALLATION AND DOWNSTREAM PACKAGING ##############
#
setupasfolder:
	@mkdir -p ./mods/as

version: VERSION mods/*/mod.yaml
ifeq ($(VERSION),)
	$(error Unable to determine new version (requires git or override of variable VERSION))
endif
	@sh -c '. ./packaging/functions.sh; set_engine_version "$(VERSION)" .'
	@sh -c '. ./packaging/functions.sh; set_mod_version "$(VERSION)" mods/*/mod.yaml'

install:
	@sh -c '. ./packaging/functions.sh; install_assemblies $(CWD) $(DESTDIR)$(gameinstalldir) $(TARGETPLATFORM) True True True True'
	@sh -c '. ./packaging/functions.sh; install_data $(CWD) $(DESTDIR)$(gameinstalldir) cnc d2k ra'

install-linux-shortcuts:
	@sh -c '. ./packaging/functions.sh; install_linux_shortcuts $(CWD) "$(DESTDIR)" "$(gameinstalldir)" "$(bindir)" "$(datadir)" "$(shell head -n1 VERSION)" cnc d2k ra'

install-linux-appdata:
	@sh -c '. ./packaging/functions.sh; install_linux_appdata $(CWD) "$(DESTDIR)" "$(datadir)" cnc d2k ra'

install-man: all
	@mkdir -p $(DESTDIR)$(mandir)/man6/
	@./utility.sh all --man-page > $(DESTDIR)$(mandir)/man6/openra.6

help:
	@echo 'to compile, run:'
	@echo '  make'
	@echo
	@echo 'to compile using system libraries for native dependencies, run:'
	@echo '  make TARGETPLATFORM=unix-generic'
	@echo
	@echo 'to check the official mods for erroneous yaml files, run:'
	@echo '  make [TREAT_WARNINGS_AS_ERRORS=false] test'
	@echo
	@echo 'to check the engine and official mod dlls for code style violations, run:'
	@echo '  make check'
	@echo
	@echo 'to compile and install Red Alert, Tiberian Dawn, and Dune 2000 run:'
	@echo '  make [prefix=/foo] [TARGETPLATFORM=unix-generic] install'
	@echo
	@echo 'to compile and install Red Alert, Tiberian Dawn, and Dune 2000'
	@echo 'using system libraries for native dependencies, run:'
	@echo '   make [prefix=/foo] [bindir=/bar/bin] TARGETPLATFORM=unix-generic install'
	@echo
	@echo 'to install FreeDesktop startup scripts, desktop files, icons, and MIME metadata'
	@echo '  make install-linux-shortcuts'
	@echo
	@echo 'to install FreeDesktop AppStream metadata'
	@echo '  make install-linux-appdata'
	@echo
	@echo 'to install a Unix man page'
	@echo '  make install-man'

########################### MAKEFILE SETTINGS ##########################
#
.DEFAULT_GOAL := all

.SUFFIXES:

.PHONY: all clean check check-scripts test version install install-linux-shortcuts install-linux-appdata install-man help

.PHONY: all web build test test-web package format format-check e2e e2e-up e2e-down audit clean
all: test package

web:
	cd web && npm ci --no-audit --no-fund && npm run build

build: web
	dotnet build Jellytrim.slnx -c Release

test: build
	dotnet test Jellytrim.slnx -c Release --no-build
	cd web && npm run check && npm test

package: build
	python3 scripts/package.py

format:
	dotnet format Jellytrim.slnx

format-check:
	dotnet format Jellytrim.slnx --verify-no-changes

e2e-up: package
	python3 e2e/env.py up

e2e-down:
	python3 e2e/env.py down

e2e: e2e-up
	cd e2e && npm ci --no-audit --no-fund && npx playwright test

audit:
	dotnet list Jellytrim.slnx package --vulnerable --include-transitive
	cd web && npm audit --omit=dev
	cd e2e && npm audit --omit=dev

clean:
	rm -rf dist web/dist src/*/bin src/*/obj tests/*/bin tests/*/obj

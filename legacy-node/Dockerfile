FROM ubuntu:22.04

LABEL maintainer="arma3-server-manager"
LABEL description="Arma 3 Dedicated Server with Web Management Panel"

ENV DEBIAN_FRONTEND=noninteractive
ENV ARMA3_DIR=/arma3
ENV STEAMCMD_DIR=/steamcmd
ENV NODE_VERSION=20

# Add i386 support required by SteamCMD
RUN dpkg --add-architecture i386

# Install system dependencies
RUN apt-get update && apt-get install -y \
    lib32gcc-s1 \
    lib32stdc++6 \
    libsdl2-2.0-0 \
    libcurl4 \
    ca-certificates \
    curl \
    wget \
    unzip \
    tar \
    tini \
    locales \
    tzdata \
    xz-utils \
    && locale-gen en_US.UTF-8 \
    && update-locale LANG=en_US.UTF-8 \
    && curl -fsSL https://deb.nodesource.com/setup_${NODE_VERSION}.x | bash - \
    && apt-get install -y nodejs \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

ENV LANG=en_US.UTF-8
ENV LC_ALL=en_US.UTF-8

# Create arma3 user (avoid running as root)
RUN useradd -m -u 1000 -s /bin/bash arma3

# Install SteamCMD
RUN mkdir -p ${STEAMCMD_DIR} \
    && cd ${STEAMCMD_DIR} \
    && curl -sqL "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz" | tar zxvf - \
    && chown -R arma3:arma3 ${STEAMCMD_DIR}

# Create required directories
RUN mkdir -p \
    ${ARMA3_DIR}/mods \
    ${ARMA3_DIR}/keys \
    ${ARMA3_DIR}/config \
    ${ARMA3_DIR}/profiles \
    ${ARMA3_DIR}/missions \
    ${ARMA3_DIR}/logs \
    /home/arma3/Steam \
    /home/arma3/.steam \
    && chown -R arma3:arma3 ${ARMA3_DIR}
RUN chown -R arma3:arma3 /home/arma3/Steam /home/arma3/.steam

# Install Node.js web panel dependencies
WORKDIR /app
COPY web/package.json web/package-lock.json* ./
RUN npm install --omit=dev && chown -R arma3:arma3 /app

# Copy web panel source
COPY web/ ./
RUN chown -R arma3:arma3 /app

# Copy startup scripts
COPY scripts/ /scripts/
RUN chmod +x /scripts/*.sh && chown -R arma3:arma3 /scripts

# Copy default configs (copied at runtime if not present in volume)
COPY config/ /defaults/config/

# ─── Exposed ports ───────────────────────────────────────────────────────────
# Arma 3 game port (UDP)
EXPOSE 2302/udp
# Arma 3 server browser (UDP)
EXPOSE 2303/udp
# BattlEye RCON (UDP)
EXPOSE 2304/udp
# VON (UDP)
EXPOSE 2305/udp
# BattlEye (UDP)
EXPOSE 2306/udp
# Web Management Panel (TCP)
EXPOSE 8080/tcp

VOLUME ["${ARMA3_DIR}/mods", "${ARMA3_DIR}/config", "${ARMA3_DIR}/profiles", "${ARMA3_DIR}/missions", "/home/arma3/Steam", "/home/arma3/.steam"]

USER arma3
WORKDIR /app

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["/scripts/entrypoint.sh"]

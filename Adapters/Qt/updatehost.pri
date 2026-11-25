# Copy this adapter directory into another Qt project, then include this file.
QT += network
CONFIG += c++11
INCLUDEPATH += $$PWD
HEADERS += $$PWD/updatehost.h
SOURCES += $$PWD/updatehost.cpp
